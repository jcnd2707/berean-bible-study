using System.Text;
using System.Text.Json;
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
///   ListConversations()
///   DeleteConversation(id)
///   SendMessage(text, mode)
///   SetLanguage(language)
///   ResetConversation()
///   GetRagStatus()
///   ReindexDocuments()
///
/// Server → Client (for one answer, in this order):
///   TokenReceived("")            the answer has started
///   Sources(list)                the numbered sources the answer may cite ([S1], [ADV1], …)
///   ToolActivity(name, text)     the model is using a tool ("Looking up hesed…")
///   TokenReceived(chunk)         answer text, as it is generated
///   MessageComplete(fullText)
///
/// Other events:
///   SessionStarted(ragChunks)
///   ConversationStarted(id)
///   ConversationLoaded(id, title, modelId, messages)   reply to ResumeConversation
///   ConversationList(list)         newest first
///   ConversationDeleted(id)
///   ConversationReset()
///   RagStatus(hasIndex, chunks, details)
///   RagIndexing(message) / RagIndexed(success, message)
///   Error(message)
/// </summary>
public class ChatHub : Hub
{
    private readonly StudySessionService _sessions;
    private readonly IHubContext<ChatHub> _hubContext;
    private readonly ILogger<ChatHub> _log;

    public ChatHub(StudySessionService sessions, IHubContext<ChatHub> hubContext, ILogger<ChatHub> log)
    {
        _sessions = sessions;
        _hubContext = hubContext;
        _log = log;
    }

    // ── Connection lifecycle ───────────────────────────────────────────────

    public override async Task OnConnectedAsync()
    {
        _log.LogInformation("[Hub] Connected: {Id}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _sessions.RemoveSession(Context.ConnectionId);
        _log.LogInformation("[Hub] Disconnected: {Id}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
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
        try
        {
            var pipeline = await _sessions.StartConversationAsync(Context.ConnectionId, modelId, perspectives);
            await AnnounceSessionAsync(pipeline);
            await Clients.Caller.SendAsync("ConversationStarted", pipeline.ConversationId);
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
                resumed.Pipeline.Perspectives.Select(p => p.Id).ToList());
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Hub] ResumeConversation failed");
            await Clients.Caller.SendAsync("Error", $"Could not reopen the conversation: {ex.Message}");
        }
    }

    public async Task ListConversations()
    {
        var list = await _sessions.ListConversationsAsync();
        await Clients.Caller.SendAsync("ConversationList", list.Select(ConversationDto.From).ToList());
    }

    public async Task DeleteConversation(string conversationId)
    {
        await _sessions.DeleteConversationAsync(conversationId);
        await Clients.Caller.SendAsync("ConversationDeleted", conversationId);
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

    public async Task SendMessage(string text, string mode = "Deep")
    {
        var pipeline = _sessions.GetPipeline(Context.ConnectionId);
        if (pipeline is null)
        {
            await Clients.Caller.SendAsync("Error", "No session. Call StartSession first.");
            return;
        }

        var queryMode = Enum.TryParse<QueryMode>(mode, ignoreCase: true, out var parsed)
            ? parsed
            : QueryMode.Deep;

        _log.LogInformation("[Hub] {Id} mode={Mode} perspectives={Perspectives} → {Preview}",
            Context.ConnectionId, queryMode, pipeline.Perspectives.Count == 0 ? "-" : string.Join(",", pipeline.Perspectives.Select(p => p.Id)),
            text.Length > 60 ? text[..60] + "…" : text);

        try
        {
            // Lets the client open the answer bubble while sources are being looked up.
            await Clients.Caller.SendAsync("TokenReceived", "");

            var reply = new StringBuilder();
            await foreach (var ev in pipeline.ChatEventsAsync(text, queryMode, Context.ConnectionAborted))
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
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Hub] SendMessage failed");
            await Clients.Caller.SendAsync("Error", $"Error: {ex.Message}");
        }
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

    // ── ResetConversation ──────────────────────────────────────────────────

    /// <summary>Starts a fresh conversation. The old one stays saved and can be reopened.</summary>
    public async Task ResetConversation()
    {
        await StartConversation(null);
        await Clients.Caller.SendAsync("ConversationReset");
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
public record ConversationDto(string Id, string Title, string UpdatedAt, string? Passage, string? ModelId)
{
    public static ConversationDto From(ConversationInfo c) => new(c.Id, c.Title, c.UpdatedAt, c.Passage, c.ModelId);
}
