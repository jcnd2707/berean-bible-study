using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Berean.Agent.Api.Hubs;
using Microsoft.Extensions.AI;

namespace Berean.Agent.Api.Services;

/// <summary>A reopened conversation: its record, the live pipeline continuing it, and what to show.</summary>
public record ResumedConversation(ConversationInfo Info, StudyPipeline Pipeline, List<DisplayMessage> Messages);

/// <summary>The result of ending a full session and starting the next part of the same study (Phase 5).</summary>
public record ContinuedSession(string NewConversationId, string OldConversationId, string OldTitle, string Recap);

/// <summary>
/// One <see cref="StudyPipeline"/> (the live conversation) per SignalR connection, all on one
/// shared <see cref="BibleKnowledge"/>: the index is loaded into memory once, not per connection.
///
/// Conversations are saved as they happen (see <see cref="ConversationStore"/>), so the live
/// pipeline is disposable: a refresh, a reconnect or a restart reopens the conversation from
/// its stored messages instead of losing it.
/// </summary>
public partial class StudySessionService
{
    private readonly ILoggerFactory _logFactory;
    private readonly ILogger _log;
    private readonly ModelRegistryService _models;
    private readonly LlmConfig _llm;
    private readonly RetrievalOptions? _bibleRagConfig;
    private readonly string _ollamaEndpoint;
    private readonly string _embeddingModel;
    private readonly CancellationToken _appStopping;
    private readonly ConversationStore _store;

    private readonly ConcurrentDictionary<string, StudyPipeline> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _languages = new();
    private readonly ConcurrentDictionary<string, string> _profiles = new();

    private readonly object _knowledgeGate = new();
    private Task<BibleKnowledge>? _knowledge;
    private readonly BereanResourceApiClient? _resourceApi;
    private readonly SessionLimits _limits;

    public StudySessionService(
        ILoggerFactory logFactory, IConfiguration config, ModelRegistryService models,
        SessionLimits limits, IHostApplicationLifetime lifetime)
    {
        _logFactory = logFactory;
        _log = logFactory.CreateLogger<StudySessionService>();
        _models = models;
        _llm = models.Llm;
        _limits = limits;
        _ollamaEndpoint = config["Ollama:Endpoint"] ?? "http://localhost:11434";
        _embeddingModel = config["Ollama:EmbeddingModel"] ?? "mxbai-embed-large";
        _appStopping = lifetime.ApplicationStopping;
        _bibleRagConfig = config.GetSection("Agents:BibleAgent").Get<RetrievalOptions>();
        if (_bibleRagConfig is not null)
        {
            // "Perspectives" and "MaxPerspectivesPerQuestion" are root-level config, not children
            // of "Agents:BibleAgent" — bound here rather than duplicating RetrievalOptions per API.
            _bibleRagConfig.Perspectives = config.GetSection("Perspectives").Get<List<Perspective>>() ?? [];
            _bibleRagConfig.MaxPerspectivesPerQuestion = config.GetValue("MaxPerspectivesPerQuestion", 1);

            // The Agent API doesn't store profiles — it just checks the id with the Resource API,
            // which already owns them (PROFILES_AND_SESSIONS_PLAN.md D2), reusing the same URL
            // BibleKnowledge uses for retrieval.
            _resourceApi = new BereanResourceApiClient(_bibleRagConfig.ResourceApiBaseUrl);
        }

        // chat.db lives next to the vector index.
        var indexDir = Path.GetDirectoryName(Path.GetFullPath(_bibleRagConfig?.RagDbPath ?? "index/bible.rag.db"))!;
        _store = new ConversationStore(Path.Combine(indexDir, "chat.db"), logFactory.CreateLogger<ConversationStore>());
    }

    // ── Language ───────────────────────────────────────────────────────────

    /// <summary>Set the preferred language for a connection ("en" or "es").</summary>
    public void SetLanguage(string connectionId, string language)
    {
        _languages[connectionId] = language;
        _log.LogInformation("[Session] {ConnId} language set to {Lang}", connectionId, language);
    }

    public string GetLanguage(string connectionId) =>
        _languages.TryGetValue(connectionId, out var l) ? l : "en";

    // ── Profile ────────────────────────────────────────────────────────────

    /// <summary>Checked once per connection in ChatHub.OnConnectedAsync (D2) — the Resource API owns profiles, this is one call to it.</summary>
    public async Task<bool> ProfileExistsAsync(string profileId) =>
        _resourceApi is not null && await _resourceApi.GetProfileAsync(profileId) is not null;

    /// <summary>Set once in ChatHub.OnConnectedAsync, after the profile id in the query string has been checked to exist.</summary>
    public void SetProfile(string connectionId, string profileId) => _profiles[connectionId] = profileId;

    /// <summary>Every method below that touches chat.db needs this — a connection with no profile set is a bug, not a 404.</summary>
    public string GetProfile(string connectionId) =>
        _profiles.TryGetValue(connectionId, out var id)
            ? id
            : throw new InvalidOperationException($"Connection {connectionId} has no profile set.");

    // ── Shared library ─────────────────────────────────────────────────────

    /// <summary>The shared library, built on first use. A failed start is not cached, so it can be retried.</summary>
    public Task<BibleKnowledge> GetKnowledgeAsync()
    {
        if (_bibleRagConfig is null)
            throw new InvalidOperationException("Agents:BibleAgent is not configured in appsettings.json.");

        lock (_knowledgeGate)
        {
            if (_knowledge is { IsFaulted: false, IsCanceled: false }) return _knowledge;

            _knowledge = BibleKnowledge.CreateAsync(
                _bibleRagConfig, _logFactory, _embeddingModel, _ollamaEndpoint, _appStopping);
            return _knowledge;
        }
    }

    // ── Perspectives ───────────────────────────────────────────────────────

    /// <summary>Every perspective configured for this deployment (possibly empty).</summary>
    public IReadOnlyList<Perspective> GetPerspectives() => _bibleRagConfig?.Perspectives ?? [];

    /// <summary>
    /// Resolves ids the client sent against what's configured, enforcing the per-conversation cap.
    /// On resume (<paramref name="strict"/> false) an id that no longer matches a configured
    /// perspective is silently dropped instead of failing the resume.
    /// </summary>
    private List<Perspective> ResolvePerspectives(IReadOnlyList<string>? ids, bool strict = true)
    {
        if (ids is null or { Count: 0 }) return [];

        var configured = GetPerspectives();
        var cap = _bibleRagConfig?.MaxPerspectivesPerQuestion ?? 1;
        if (strict && ids.Count > cap)
            throw new ArgumentException($"At most {cap} perspective(s) may be selected for a conversation.");

        var resolved = new List<Perspective>();
        foreach (var id in ids.Distinct())
        {
            var p = configured.FirstOrDefault(c => c.Id == id);
            if (p is not null) resolved.Add(p);
            else if (strict) throw new ArgumentException($"Unknown perspective '{id}'.");
        }
        return resolved;
    }

    // ── Conversations ──────────────────────────────────────────────────────

    /// <summary>
    /// Starts a new conversation on this connection. The perspective selection is locked for the
    /// conversation's lifetime — see <see cref="RetrievalOptions.MaxPerspectivesPerQuestion"/>.
    /// It is saved once the first question is answered.
    /// </summary>
    public async Task<StudyPipeline> StartConversationAsync(
        string connectionId, string? modelId = null, IReadOnlyList<string>? perspectiveIds = null)
    {
        _log.LogInformation("[Session] {ConnId} starting a conversation", connectionId);

        var perspectives = ResolvePerspectives(perspectiveIds);
        var pipeline = await BuildPipelineAsync(connectionId, modelId);
        pipeline.ConversationId = Guid.NewGuid().ToString("N");
        pipeline.Perspectives = perspectives;
        Persist(pipeline, modelId, GetProfile(connectionId));

        _sessions[connectionId] = pipeline;
        return pipeline;
    }

    /// <summary>
    /// Reopens a saved conversation on this connection: the stored messages are replayed to the
    /// model (or, with Claude Code, its CLI session is resumed — with a transcript as the fallback
    /// when that session is gone). Returns null if there is no such conversation for this profile —
    /// a stale id, or one belonging to the other profile, is indistinguishable from "not found".
    /// </summary>
    public async Task<ResumedConversation?> ResumeConversationAsync(string connectionId, string conversationId)
    {
        var profileId = GetProfile(connectionId);
        var info = await _store.GetAsync(conversationId, profileId);
        if (info is null) return null;

        var stored = await _store.GetMessagesAsync(conversationId);
        var pipeline = await BuildPipelineAsync(connectionId, info.ModelId);
        pipeline.ConversationId = conversationId;
        pipeline.Perspectives = ResolvePerspectives(info.PerspectiveIds, strict: false);
        pipeline.LoadHistory(ConversationStore.ToChatMessages(stored));

        if (info.ClaudeSessionId is not null && pipeline.ClaudeCode is { } claude)
            claude.RestoreSession(conversationId, info.ClaudeSessionId, pipeline.MessageCount);

        Persist(pipeline, info.ModelId, profileId);
        _sessions[connectionId] = pipeline;

        _log.LogInformation("[Session] {ConnId} resumed '{Title}' ({Count} stored messages)",
            connectionId, info.Title, stored.Count);
        return new ResumedConversation(info, pipeline, ConversationStore.ToDisplay(stored));
    }

    public Task<List<ConversationInfo>> ListConversationsAsync(string connectionId, string? query = null) =>
        _store.ListAsync(GetProfile(connectionId), query);

    public Task<bool> DeleteConversationAsync(string connectionId, string conversationId) =>
        _store.DeleteAsync(conversationId, GetProfile(connectionId));

    public Task RenameConversationAsync(string connectionId, string conversationId, string title) =>
        _store.RenameAsync(conversationId, GetProfile(connectionId), title);

    public Task SetPinnedAsync(string connectionId, string conversationId, bool pinned) =>
        _store.SetPinnedAsync(conversationId, GetProfile(connectionId), pinned);

    public Task<int> CountUnownedConversationsAsync() => _store.CountUnownedAsync();

    public Task<int> AdoptUnownedConversationsAsync(string profileId) => _store.AdoptUnownedAsync(profileId);

    public string? CurrentConversationId(string connectionId) =>
        _sessions.TryGetValue(connectionId, out var p) ? p.ConversationId : null;

    // ── Session length limit (Phase 5) ────────────────────────────────────────

    /// <summary>
    /// Where the connection's active conversation stands against the limit — "ok" for a
    /// not-yet-saved brand new conversation (nothing asked yet), since it only exists in chat.db
    /// once its first question is answered.
    /// </summary>
    public async Task<LimitState> GetLimitStateAsync(string connectionId)
    {
        var pipeline = GetPipeline(connectionId);
        if (pipeline is null) return _limits.Evaluate(0, 0);

        var info = await _store.GetAsync(pipeline.ConversationId, GetProfile(connectionId));
        return _limits.Evaluate(info?.QuestionCount ?? 0, info?.ContextTokens ?? 0);
    }

    /// <summary>
    /// Ends a full session and starts the next part of the same study: writes a recap on the old
    /// session (falling back to a plain condensed transcript — no model call — if the recap call
    /// fails or comes back empty), then starts a fresh conversation with the same model and
    /// perspectives, linked back via ContinuedFromId, with the recap carried into its first turn.
    /// The new conversation is created in chat.db immediately (not only after its first answer, the
    /// way a plain new conversation is) so its title and link are never in question.
    /// </summary>
    public async Task<ContinuedSession> ContinueConversationAsync(string connectionId)
    {
        var profileId = GetProfile(connectionId);
        var oldPipeline = GetPipeline(connectionId) ?? throw new InvalidOperationException("No active session to continue.");
        var oldId = oldPipeline.ConversationId;
        var oldInfo = await _store.GetAsync(oldId, profileId)
            ?? throw new InvalidOperationException("The session to continue has no saved turns yet.");

        string recap;
        try
        {
            recap = await oldPipeline.WriteRecapAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[Session] Recap call failed for {Id} — falling back to a condensed transcript", oldId);
            recap = "";
        }
        if (string.IsNullOrWhiteSpace(recap)) recap = oldPipeline.CondensedTranscript();
        if (string.IsNullOrWhiteSpace(recap)) recap = "(Nothing was asked in the earlier part of this study.)";

        await _store.SetRecapAsync(oldId, profileId, recap);

        var newPipeline = await BuildPipelineAsync(connectionId, oldInfo.ModelId);
        var newId = Guid.NewGuid().ToString("N");
        newPipeline.ConversationId = newId;
        newPipeline.Perspectives = oldPipeline.Perspectives;
        newPipeline.CarryOver = recap;

        var now = DateTime.UtcNow.ToString("O");
        await _store.CreateAsync(new ConversationInfo(
            newId, ContinuedTitle(oldInfo.Title), now, now, oldInfo.Passage, oldInfo.ModelId, null,
            newPipeline.Perspectives.Select(p => p.Id).ToList(), profileId, ContinuedFromId: oldId));

        Persist(newPipeline, oldInfo.ModelId, profileId);
        _sessions[connectionId] = newPipeline;

        _log.LogInformation("[Session] {ConnId} continued '{Old}' as '{New}'", connectionId, oldId, newId);
        return new ContinuedSession(newId, oldId, oldInfo.Title, recap);
    }

    /// <summary>"Grace and the law" → "Grace and the law (part 2)" → "… (part 3)", and so on.</summary>
    internal static string ContinuedTitle(string oldTitle)
    {
        var m = PartSuffix().Match(oldTitle);
        return m.Success ? $"{m.Groups[1].Value} (part {int.Parse(m.Groups[2].Value) + 1})" : $"{oldTitle} (part 2)";
    }

    [GeneratedRegex(@"^(.*) \(part (\d+)\)$")]
    private static partial Regex PartSuffix();

    private async Task<StudyPipeline> BuildPipelineAsync(string connectionId, string? modelId)
    {
        var knowledge = await GetKnowledgeAsync();
        var (provider, model) = _models.Resolve(modelId);
        _log.LogInformation("[Session] {ConnId} model: {Provider}/{Model}", connectionId, provider, model);

        var llm = ChatClientFactory.Create(_llm, provider, model, _logFactory);
        return StudyPipeline.CreateBible(knowledge, llm, _llm, _logFactory, GetLanguage(connectionId));
    }

    // ── Saving ─────────────────────────────────────────────────────────────

    private void Persist(StudyPipeline pipeline, string? modelId, string profileId) =>
        pipeline.TurnCompleted = turn => SaveTurnAsync(pipeline, modelId, profileId, turn);

    /// <summary>A save that fails is logged, never allowed to break the chat.</summary>
    private async Task SaveTurnAsync(StudyPipeline pipeline, string? modelId, string profileId, TurnRecord turn)
    {
        try
        {
            var id = pipeline.ConversationId;
            var now = DateTime.UtcNow.ToString("O");
            var firstQuestion = turn.Messages.FirstOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

            if (!await _store.ExistsAsync(id))
                await _store.CreateAsync(new ConversationInfo(
                    id, MakeTitle(firstQuestion), now, now, PassageOf(firstQuestion), modelId, null,
                    pipeline.Perspectives.Select(p => p.Id).ToList(), profileId));

            // The sources ride on the last answer message of the turn.
            var sourcesJson = turn.Retrieval is { Sources.Count: > 0 } r
                ? SerializeSources(r.Sources)
                : null;
            var lastAnswer = turn.Messages.ToList().FindLastIndex(m => m.Role == ChatRole.Assistant && m.Text.Length > 0);

            var stored = turn.Messages.Select((m, i) => new StoredMessage(
                RoleName(m.Role),
                m.Role == ChatRole.User ? QueryRouter.StripContextTags(m.Text) : m.Text,
                ConversationStore.Serialize(m),
                i == lastAnswer ? sourcesJson : null,
                now));

            await _store.AppendAsync(id, stored, pipeline.ClaudeCode?.SessionIdFor(id), pipeline.LastLocationJson, turn.ContextTokens);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Session] Could not save the conversation");
        }
    }

    /// <summary>
    /// The sources of an answer as stored (and later sent back to the client as they are), so the
    /// names must be the camelCase the client already uses for live answers.
    /// </summary>
    internal static string SerializeSources(IEnumerable<ContextSource> sources) =>
        JsonSerializer.Serialize(sources.Select(SourceDto.From), JsonSerializerOptions.Web);

    private static string RoleName(ChatRole role) =>
        role == ChatRole.User ? "user" : role == ChatRole.Assistant ? "assistant" : role == ChatRole.Tool ? "tool" : role.Value;

    /// <summary>The first question, clipped at a word, as the conversation's title.</summary>
    internal static string MakeTitle(string firstMessage)
    {
        var text = string.Join(' ', QueryRouter.StripContextTags(firstMessage)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0) return "New conversation";
        if (text.Length <= 60) return text;

        var cut = text.LastIndexOf(' ', 60);
        return text[..(cut > 30 ? cut : 60)] + "…";
    }

    /// <summary>"John chapter 3", from the client's [Passage: …] tag on the first message.</summary>
    internal static string? PassageOf(string firstMessage)
    {
        var m = PassageTag().Match(firstMessage);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    [GeneratedRegex(@"\[Passage:\s*([^\]]+)\]")]
    private static partial Regex PassageTag();

    // ── Session lifecycle ──────────────────────────────────────────────────

    public StudyPipeline? GetPipeline(string connectionId) =>
        _sessions.TryGetValue(connectionId, out var p) ? p : null;

    public bool HasSession(string connectionId) => _sessions.ContainsKey(connectionId);

    public void RemoveSession(string connectionId)
    {
        _sessions.TryRemove(connectionId, out _);
        _languages.TryRemove(connectionId, out _);
        _profiles.TryRemove(connectionId, out _);
        _log.LogInformation("[Session] {ConnId} removed", connectionId);
    }

    /// <summary>
    /// Indexes any module that is in the library but not (fully) in the index. Nothing already
    /// indexed is deleted or re-embedded.
    /// </summary>
    public async Task<RagIndexResult> IndexPendingAsync()
    {
        var knowledge = await GetKnowledgeAsync();
        var count = await knowledge.StartIndexingAsync(_log, _appStopping);
        return count == 0
            ? new RagIndexResult(true, "Index already up to date.")
            : new RagIndexResult(true, $"Indexing {count} module(s) in the background.");
    }

    public async Task<RagStatusResult> GetRagStatusAsync(string connectionId)
    {
        if (GetPipeline(connectionId) is null) return new RagStatusResult(false, 0, "No session");

        var k = await GetKnowledgeAsync();
        var pending = k.PendingModules.Count > 0 ? $" | Not indexed: {string.Join(", ", k.PendingModules)}" : "";
        return new RagStatusResult(k.IndexedChunks > 0, k.IndexedChunks,
            $"Lang: {GetLanguage(connectionId)}{pending}");
    }
}

public record RagIndexResult(bool Success, string Message);
public record RagStatusResult(bool HasIndex, int ChunkCount, string Details);
