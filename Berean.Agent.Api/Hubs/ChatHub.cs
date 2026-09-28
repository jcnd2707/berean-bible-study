using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Berean.Agent.Api.Services;
using Microsoft.AspNetCore.SignalR;

namespace Berean.Agent.Api.Hubs;

/// <summary>
/// SignalR hub — one persistent connection per client window.
///
/// Client → Server:
///   StartConversation(modelId?, perspectives?)   a new conversation (StartSession is the same);
///                                                 perspectives is locked for the conversation
///   ResumeConversation(id)         reopen a saved conversation (keeps its perspective selection)
///   ListConversations(query?)      pinned first, then most recently used; query matches title or
///                                   the profile's own questions
///   DeleteConversation(id)
///   RenameConversation(id, title)
///   SetPinned(id, pinned)
///   SendMessage(text, mode, location?)   location is {moduleId,book,chapter,verse} or null;
///                                         refused once the session is full (Phase 5), or if an
///                                         answer is already running on this connection
///   CancelMessage()                 stops the answer currently streaming on this connection, if any
///   ContinueConversation()         ends a full session and starts the next part of the same study
///   SetLanguage(language)
///   GetRagStatus()
///   ReindexDocuments()
///
/// There is no "reset" method — a new conversation is StartConversation(modelId, perspectives),
/// called directly by the client, so it never loses the model/perspective selection the way a
/// no-argument reset used to.
///
/// Server → Client (for one answer, in this order):
///   TokenReceived("")            the answer has started
///   Sources(list)                the numbered sources the answer may cite ([S1], [ADV1], …)
///   ToolActivity(name, text)     the model is using a tool ("Looking up hesed…")
///   TokenReceived(chunk)         answer text, as it is generated
///   MessageComplete(fullText)
///   MessageStopped()             the answer was stopped (CancelMessage, or a conversation switch
///                                 while one was streaming) — never sent for a dropped connection
///
/// Other events:
///   SessionStarted(ragChunks)
///   ConversationStarted(id)
///   ConversationLoaded(id, title, modelId, messages, perspectives, lastLocation, pinned)   reply to ResumeConversation
///   ConversationList(list)         pinned first, then most recently used
///   ConversationDeleted(id)
///   SessionLimit(state)            sent after StartConversation/ResumeConversation/MessageComplete —
///                                   {questionsUsed,maxQuestions,contextTokens,maxContextTokens,state}
///   ConversationContinued(previousId, previousTitle, recap)   sent alongside ContinueConversation's
///                                                              own ConversationStarted(newId)
///   RagStatus(hasIndex, chunks, details)
///   RagIndexing(message) / RagIndexed(success, message)
///   Error(message)
/// </summary>
public partial class ChatHub : Hub
{
    private readonly StudySessionService _sessions;
    private readonly SessionLimits _limits;
    private readonly ActiveAnswers _activeAnswers;
    private readonly IHubContext<ChatHub> _hubContext;
    private readonly ILogger<ChatHub> _log;

    /// <summary>How long a conversation switch waits for a running answer to stop before proceeding anyway.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    public ChatHub(
        StudySessionService sessions, SessionLimits limits, ActiveAnswers activeAnswers,
        IHubContext<ChatHub> hubContext, ILogger<ChatHub> log)
    {
        _sessions = sessions;
        _limits = limits;
        _activeAnswers = activeAnswers;
        _hubContext = hubContext;
        _log = log;
    }

    // ── Connection lifecycle ───────────────────────────────────────────────

    /// <summary>
    /// The profile travels as a query param (browsers can't set headers on a WebSocket — D3),
    /// checked once here against the Resource API, which owns profiles (D2). This is separation,
    /// not access control (D1): the check exists so a stale or missing profile fails fast with a
    /// clear message instead of every later call throwing "no profile set".
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        var profileId = Context.GetHttpContext()?.Request.Query["profile"].ToString();

        if (string.IsNullOrEmpty(profileId) || !ProfileIdFormat().IsMatch(profileId) || !await _sessions.ProfileExistsAsync(profileId))
        {
            _log.LogWarning("[Hub] Rejected {Id}: no valid profile in the connection URL", Context.ConnectionId);
            await Clients.Caller.SendAsync("Error", "Choose a profile first.");
            Context.Abort();
            return;
        }

        _sessions.SetProfile(Context.ConnectionId, profileId);
        _log.LogInformation("[Hub] Connected: {Id}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex ProfileIdFormat();

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _activeAnswers.Cancel(Context.ConnectionId);
        _sessions.RemoveSession(Context.ConnectionId);
        _log.LogInformation("[Hub] Disconnected: {Id}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Cancels the running answer, if any, and waits for it to finish sending — see
    /// ActiveAnswers.WaitForIdleAsync for why. Called first by every hub method that changes what
    /// conversation this connection is on, so the conversation can never change under a running answer.
    /// </summary>
    private async Task StopActiveAnswerAsync()
    {
        if (_activeAnswers.Cancel(Context.ConnectionId))
            await _activeAnswers.WaitForIdleAsync(Context.ConnectionId, StopTimeout);
    }

    // ── Conversations ──────────────────────────────────────────────────────

    /// <summary>
    /// Starts a new conversation on the model with this id (null = the default). The perspective
    /// selection (capped by "MaxPerspectivesPerQuestion") is locked for the conversation's
    /// lifetime — send an unknown id or too many and the conversation isn't started. Saved after
    /// its first answer.
    /// </summary>
    public async Task StartConversation(string? modelId = null, string[]? perspectives = null)
    {
        await StopActiveAnswerAsync();
        try
        {
            var pipeline = await _sessions.StartConversationAsync(Context.ConnectionId, modelId, perspectives);
            await AnnounceSessionAsync(pipeline);
            await Clients.Caller.SendAsync("ConversationStarted", pipeline.ConversationId);
            await SendLimitStateAsync();
        }
        catch (ArgumentException ex)
        {
            await Clients.Caller.SendAsync("Error", ex.Message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Hub] StartConversation failed");
            await Clients.Caller.SendAsync("Error", $"Failed to start the session: {ex.Message}");
        }
    }

    /// <summary>Same as <see cref="StartConversation"/>.</summary>
    public Task StartSession(string? modelId = null, string[]? perspectives = null) => StartConversation(modelId, perspectives);

    /// <summary>Reopens a saved conversation and sends its messages back for display.</summary>
    public async Task ResumeConversation(string conversationId)
    {
        await StopActiveAnswerAsync();
        try
        {
            var resumed = await _sessions.ResumeConversationAsync(Context.ConnectionId, conversationId);
            if (resumed is null)
            {
                // Deleted, or the chat database was replaced: carry on with a fresh conversation.
                await StartConversation(null);
                return;
            }

            await AnnounceSessionAsync(resumed.Pipeline);
            await Clients.Caller.SendAsync("ConversationLoaded",
                resumed.Info.Id, resumed.Info.Title, resumed.Info.ModelId, resumed.Messages,
                resumed.Pipeline.Perspectives.Select(p => p.Id).ToList(), resumed.Info.LastLocation, resumed.Info.Pinned);
            await SendLimitStateAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Hub] ResumeConversation failed");
            await Clients.Caller.SendAsync("Error", $"Could not reopen the conversation: {ex.Message}");
        }
    }

    public async Task ListConversations(string? query = null)
    {
        var list = await _sessions.ListConversationsAsync(Context.ConnectionId, query);
        await Clients.Caller.SendAsync("ConversationList", list.Select(c => ConversationDto.From(c, _limits)).ToList());
    }

    public async Task DeleteConversation(string conversationId)
    {
        if (_sessions.CurrentConversationId(Context.ConnectionId) == conversationId)
            await StopActiveAnswerAsync();
        await _sessions.DeleteConversationAsync(Context.ConnectionId, conversationId);
        await Clients.Caller.SendAsync("ConversationDeleted", conversationId);
    }

    public async Task RenameConversation(string conversationId, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        await _sessions.RenameConversationAsync(Context.ConnectionId, conversationId, title.Trim());
        await ListConversations();
    }

    public async Task SetPinned(string conversationId, bool pinned)
    {
        await _sessions.SetPinnedAsync(Context.ConnectionId, conversationId, pinned);
        await ListConversations();
    }

    private async Task AnnounceSessionAsync(StudyPipeline pipeline)
    {
        var status = await _sessions.GetRagStatusAsync(Context.ConnectionId);
        await Clients.Caller.SendAsync("SessionStarted", status.ChunkCount);

        if (pipeline.IsIndexing)
        {
            await Clients.Caller.SendAsync("RagIndexing", "Building index in background — chat is available now.");
            AttachIndexingContinuation(pipeline.IndexingTask, () => pipeline.IndexedChunks, Context.ConnectionId);
        }
    }

    // ── SendMessage ────────────────────────────────────────────────────────

    public async Task SendMessage(string text, string mode = "Deep", LocationDto? location = null)
    {
        var pipeline = _sessions.GetPipeline(Context.ConnectionId);
        if (pipeline is null)
        {
            await Clients.Caller.SendAsync("Error", "No session. Call StartSession first.");
            return;
        }

        // The enforcement (D7): a session at its limit answers no further questions. The client
        // also disables its input on "full", but this is what actually stops it.
        var limitState = await _sessions.GetLimitStateAsync(Context.ConnectionId);
        if (limitState.State == "full")
        {
            await Clients.Caller.SendAsync("SessionLimit", limitState);
            return;
        }

        // Tracks where the study *ends*, not just where it began (Passage, set from the first
        // message only) — read back by SaveTurnAsync once this turn completes.
        if (location is not null)
            pipeline.LastLocationJson = JsonSerializer.Serialize(location, JsonSerializerOptions.Web);

        var queryMode = Enum.TryParse<QueryMode>(mode, ignoreCase: true, out var parsed)
            ? parsed
            : QueryMode.Deep;

        var token = _activeAnswers.TryBegin(Context.ConnectionId, Context.ConnectionAborted);
        if (token is null)
        {
            await Clients.Caller.SendAsync("Error", "An answer is already in progress.");
            return;
        }

        _log.LogInformation("[Hub] {Id} mode={Mode} perspectives={Perspectives} → {Preview}",
            Context.ConnectionId, queryMode, pipeline.Perspectives.Count == 0 ? "-" : string.Join(",", pipeline.Perspectives.Select(p => p.Id)),
            text.Length > 60 ? text[..60] + "…" : text);

        try
        {
            // Lets the client open the answer bubble while sources are being looked up.
            await Clients.Caller.SendAsync("TokenReceived", "");

            var reply = new StringBuilder();
            await foreach (var ev in pipeline.ChatEventsAsync(text, queryMode, token.Value))
            {
                switch (ev)
                {
                    case SourcesEvent s when s.Retrieval.Sources.Count > 0:
                        await Clients.Caller.SendAsync("Sources", s.Retrieval.Sources.Select(SourceDto.From).ToList());
                        break;

                    case ToolEvent t:
                        await Clients.Caller.SendAsync("ToolActivity", t.Name, DescribeTool(t.Name, t.Arguments));
                        break;

                    case TextEvent x:
                        reply.Append(x.Text);
                        await Clients.Caller.SendAsync("TokenReceived", x.Text);
                        break;
                }
            }

            await Clients.Caller.SendAsync("MessageComplete", reply.Length > 0 ? reply.ToString() : "(no response)");
            await SendLimitStateAsync();
        }
        catch (OperationCanceledException)
        {
            // The turn already rolled back cleanly inside the agent (StudyAgent's finally) — never
            // saved, never counted toward the question limit. The one remaining leak is Claude
            // Code's own CLI session, which may already have the stopped question written into it;
            // clear the stored session so a reload can't --resume it (ClaudeCodeChatClient's
            // in-memory cache is fixed by its own try/finally for the rest of this process's life).
            await ClearClaudeSessionAfterStopAsync(pipeline.ConversationId);

            // Nothing to send to a connection that's already gone — and a dropped-connection
            // cancellation was never "stopped" from the user's point of view.
            if (!Context.ConnectionAborted.IsCancellationRequested)
                await Clients.Caller.SendAsync("MessageStopped");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Hub] SendMessage failed");
            await Clients.Caller.SendAsync("Error", $"Error: {ex.Message}");
        }
        finally
        {
            _activeAnswers.End(Context.ConnectionId);
        }
    }

    /// <summary>Stops the answer currently streaming on this connection, if any (D11).</summary>
    public void CancelMessage()
    {
        _activeAnswers.Cancel(Context.ConnectionId);
    }

    private async Task ClearClaudeSessionAfterStopAsync(string conversationId)
    {
        try
        {
            await _sessions.ClearClaudeSessionAsync(Context.ConnectionId, conversationId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Hub] Could not clear the Claude Code session after a stop");
        }
    }

    // ── ContinueConversation ───────────────────────────────────────────────

    /// <summary>Ends a full session and starts the next part of the same study, recap carried over (Phase 5).</summary>
    public async Task ContinueConversation()
    {
        await StopActiveAnswerAsync();
        try
        {
            var result = await _sessions.ContinueConversationAsync(Context.ConnectionId);
            var pipeline = _sessions.GetPipeline(Context.ConnectionId)!;
            await AnnounceSessionAsync(pipeline);
            await Clients.Caller.SendAsync("ConversationStarted", result.NewConversationId);
            await Clients.Caller.SendAsync("ConversationContinued", result.OldConversationId, result.OldTitle, result.Recap);
            await SendLimitStateAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Hub] ContinueConversation failed");
            await Clients.Caller.SendAsync("Error", $"Could not continue the session: {ex.Message}");
        }
    }

    private async Task SendLimitStateAsync()
    {
        var state = await _sessions.GetLimitStateAsync(Context.ConnectionId);
        await Clients.Caller.SendAsync("SessionLimit", state);
    }

    // ── SetLanguage ────────────────────────────────────────────────────────

    /// <summary>Set preferred language for this connection ("en" or "es").</summary>
    public async Task SetLanguage(string language)
    {
        var valid = language is "en" or "es";
        if (!valid) language = "en";

        _sessions.SetLanguage(Context.ConnectionId, language);
        await Clients.Caller.SendAsync("LanguageSet", language);
        _log.LogInformation("[Hub] {ConnId} language={Lang}", Context.ConnectionId, language);
    }

    // ── GetRagStatus ───────────────────────────────────────────────────────

    public async Task GetRagStatus()
    {
        var s = await _sessions.GetRagStatusAsync(Context.ConnectionId);
        await Clients.Caller.SendAsync("RagStatus", s.HasIndex, s.ChunkCount, s.Details);
    }

    // ── ReindexDocuments ───────────────────────────────────────────────────

    /// <summary>Indexes any module that isn't in the index yet. Existing chunks are kept.</summary>
    public async Task ReindexDocuments()
    {
        if (!_sessions.HasSession(Context.ConnectionId))
        {
            await Clients.Caller.SendAsync("Error", "No session.");
            return;
        }

        try
        {
            var result = await _sessions.IndexPendingAsync();
            var knowledge = await _sessions.GetKnowledgeAsync();

            if (knowledge.IsIndexing)
            {
                await Clients.Caller.SendAsync("RagIndexing", result.Message);
                AttachIndexingContinuation(knowledge.IndexingTask, () => knowledge.IndexedChunks, Context.ConnectionId);
            }
            else
            {
                await Clients.Caller.SendAsync("RagIndexed", true, result.Message);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Hub] ReindexDocuments failed");
            await Clients.Caller.SendAsync("Error", $"Indexing failed: {ex.Message}");
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>"Looking up hesed…" for the UI, from the tool name and its JSON arguments.</summary>
    internal static string DescribeTool(string name, string argsJson)
    {
        string Arg(string key)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                return doc.RootElement.TryGetProperty(key, out var v) ? v.ToString() : "";
            }
            catch (JsonException) { return ""; }
        }

        return name switch
        {
            "lookup_word" => $"Looking up {Arg("word")}…",
            "get_cross_references" => $"Finding cross-references for {Arg("verse")}…",
            "lookup_verse" => $"Reading {Arg("book")} {Arg("chapter")}:{Arg("verse")}…",
            "get_passage" => $"Reading {Arg("book")} {Arg("chapter")}…",
            "find_word_occurrences" => $"Counting uses of {Arg("strongs")}…",
            _ => $"Using {name}…",
        };
    }

    private void AttachIndexingContinuation(Task indexingTask, Func<int> chunkCount, string connectionId)
    {
        var hubContext = _hubContext;
        var log = _log;

        _ = indexingTask.ContinueWith(async t =>
        {
            var client = hubContext.Clients.Client(connectionId);
            if (t.IsCanceled) return; // shutting down — don't push anything
            if (t.IsFaulted)
            {
                log.LogError(t.Exception, "[Hub] Background indexing failed for {ConnId}", connectionId);
                await client.SendAsync("RagIndexed", false,
                    $"Indexing failed: {t.Exception?.InnerException?.Message ?? t.Exception?.Message}");
                return;
            }
            await client.SendAsync("RagIndexed", true, $"Index ready — {chunkCount()} chunks");
        }, TaskScheduler.Default);
    }
}

/// <summary>A numbered source, as the web client needs it to render a clickable citation.</summary>
public record SourceDto(
    string Id,
    string Kind,
    string ModuleId,
    string DisplayName,
    string Tradition,
    string? Era,
    string Label,
    string? Book,
    int? BookNumber,
    int? Chapter,
    int? Verse,
    int? BookChapterIndex)
{
    public static SourceDto From(ContextSource s) => new(
        s.Id, s.Kind, s.ModuleId, s.DisplayName, s.Tradition, s.Era, s.Label,
        s.BookNumber is int b ? BibleBookMap.GetFullName(b) : null,
        s.BookNumber, s.Chapter, s.Verse, s.BookChapterIndex);
}

/// <summary>A saved conversation, as the conversation list shows it.</summary>
public record ConversationDto(
    string Id, string Title, string UpdatedAt, string? Passage, string? ModelId, string? LastLocation, bool Pinned,
    bool Full, string? Recap)
{
    public static ConversationDto From(ConversationInfo c, SessionLimits limits) => new(
        c.Id, c.Title, c.UpdatedAt, c.Passage, c.ModelId, c.LastLocation, c.Pinned,
        limits.Evaluate(c.QuestionCount, c.ContextTokens ?? 0).State == "full",
        c.Recap);
}

/// <summary>Where the reader was when a message was sent — the client's BibleLocation, one to one.</summary>
public record LocationDto(string? ModuleId, string? Book, int? Chapter, int? Verse);
