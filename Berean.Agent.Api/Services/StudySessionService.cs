using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Berean.Agent.Api.Hubs;
using Microsoft.Extensions.AI;

namespace Berean.Agent.Api.Services;

/// <summary>A reopened conversation: its record, the live pipeline continuing it, and what to show.</summary>
public record ResumedConversation(ConversationInfo Info, StudyPipeline Pipeline, List<DisplayMessage> Messages);

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

    private readonly object _knowledgeGate = new();
    private Task<BibleKnowledge>? _knowledge;

    public StudySessionService(
        ILoggerFactory logFactory, IConfiguration config, ModelRegistryService models,
        IHostApplicationLifetime lifetime)
    {
        _logFactory = logFactory;
        _log = logFactory.CreateLogger<StudySessionService>();
        _models = models;
        _llm = models.Llm;
        _ollamaEndpoint = config["Ollama:Endpoint"] ?? "http://localhost:11434";
        _embeddingModel = config["Ollama:EmbeddingModel"] ?? "mxbai-embed-large";
        _appStopping = lifetime.ApplicationStopping;
        _bibleRagConfig = config.GetSection("Agents:BibleAgent").Get<RetrievalOptions>();

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

    // ── Conversations ──────────────────────────────────────────────────────

    /// <summary>Starts a new conversation on this connection. It is saved once the first question is answered.</summary>
    public async Task<StudyPipeline> StartConversationAsync(string connectionId, string? modelId = null)
    {
        _log.LogInformation("[Session] {ConnId} starting a conversation", connectionId);

        var pipeline = await BuildPipelineAsync(connectionId, modelId);
        pipeline.ConversationId = Guid.NewGuid().ToString("N");
        Persist(pipeline, modelId);

        _sessions[connectionId] = pipeline;
        return pipeline;
    }

    /// <summary>
    /// Reopens a saved conversation on this connection: the stored messages are replayed to the
    /// model (or, with Claude Code, its CLI session is resumed — with a transcript as the fallback
    /// when that session is gone). Returns null if there is no such conversation.
    /// </summary>
    public async Task<ResumedConversation?> ResumeConversationAsync(string connectionId, string conversationId)
    {
        var info = await _store.GetAsync(conversationId);
        if (info is null) return null;

        var stored = await _store.GetMessagesAsync(conversationId);
        var pipeline = await BuildPipelineAsync(connectionId, info.ModelId);
        pipeline.ConversationId = conversationId;
        pipeline.LoadHistory(ConversationStore.ToChatMessages(stored));

        if (info.ClaudeSessionId is not null && pipeline.ClaudeCode is { } claude)
            claude.RestoreSession(conversationId, info.ClaudeSessionId, pipeline.MessageCount);

        Persist(pipeline, info.ModelId);
        _sessions[connectionId] = pipeline;

        _log.LogInformation("[Session] {ConnId} resumed '{Title}' ({Count} stored messages)",
            connectionId, info.Title, stored.Count);
        return new ResumedConversation(info, pipeline, ConversationStore.ToDisplay(stored));
    }

    public Task<List<ConversationInfo>> ListConversationsAsync() => _store.ListAsync();

    public Task<bool> DeleteConversationAsync(string conversationId) => _store.DeleteAsync(conversationId);

    public string? CurrentConversationId(string connectionId) =>
        _sessions.TryGetValue(connectionId, out var p) ? p.ConversationId : null;

    private async Task<StudyPipeline> BuildPipelineAsync(string connectionId, string? modelId)
    {
        var knowledge = await GetKnowledgeAsync();
        var (provider, model) = _models.Resolve(modelId);
        _log.LogInformation("[Session] {ConnId} model: {Provider}/{Model}", connectionId, provider, model);

        var llm = ChatClientFactory.Create(_llm, provider, model, _logFactory);
        return StudyPipeline.CreateBible(knowledge, llm, _llm, _logFactory, GetLanguage(connectionId));
    }

    // ── Saving ─────────────────────────────────────────────────────────────

    private void Persist(StudyPipeline pipeline, string? modelId) =>
        pipeline.TurnCompleted = turn => SaveTurnAsync(pipeline, modelId, turn);

    /// <summary>A save that fails is logged, never allowed to break the chat.</summary>
    private async Task SaveTurnAsync(StudyPipeline pipeline, string? modelId, TurnRecord turn)
    {
        try
        {
            var id = pipeline.ConversationId;
            var now = DateTime.UtcNow.ToString("O");
            var firstQuestion = turn.Messages.FirstOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

            if (!await _store.ExistsAsync(id))
                await _store.CreateAsync(new ConversationInfo(
                    id, MakeTitle(firstQuestion), now, now, PassageOf(firstQuestion), modelId, null));

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

            await _store.AppendAsync(id, stored, pipeline.ClaudeCode?.SessionIdFor(id));
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
