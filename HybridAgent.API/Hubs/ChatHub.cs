using HybridAgent.API.Services;
using Microsoft.AspNetCore.SignalR;

namespace HybridAgent.API.Hubs;

/// <summary>
/// SignalR hub — one persistent connection per client window.
///
/// Client → Server:
///   SelectAgent(agentType)
///   SendMessage(text)
///   GetVerdict()
///   ResetConversation()
///   GetRagStatus()
///   ReindexDocuments()
///
/// Server → Client:
///   AgentSelected(type, cloudAvailable, ragChunks)
///   TokenReceived(token)
///   MessageComplete(fullText)
///   VerdictComplete(verdictText)
///   ConversationReset()
///   RagStatus(hasIndex, chunks, details)
///   RagIndexing(message)
///   RagIndexed(success, message)
///   Error(message)
/// </summary>
public class ChatHub : Hub
{
    private readonly AgentSessionService _sessions;
    private readonly IHubContext<ChatHub> _hubContext;
    private readonly ILogger<ChatHub> _log;

    public ChatHub(AgentSessionService sessions, IHubContext<ChatHub> hubContext, ILogger<ChatHub> log)
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

    // ── SelectAgent ────────────────────────────────────────────────────────

    public async Task SelectAgent(string agentType)
    {
        if (!Enum.TryParse<AgentType>(agentType, ignoreCase: true, out var type))
        {
            await Clients.Caller.SendAsync("Error", $"Unknown agent type: {agentType}");
            return;
        }

        try
        {
            var pipeline = await _sessions.SelectAgentAsync(
                Context.ConnectionId, type, Context.ConnectionAborted);

            var status = _sessions.GetRagStatus(Context.ConnectionId);

            await Clients.Caller.SendAsync("AgentSelected",
                agentType, pipeline.CloudAvailable, status.ChunkCount);

            if (pipeline.IsIndexing)
            {
                await Clients.Caller.SendAsync("RagIndexing",
                    "Building index in background — chat is available now.");

                AttachIndexingContinuation(pipeline, Context.ConnectionId);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Hub] SelectAgent failed");
            await Clients.Caller.SendAsync("Error", $"Failed to load agent: {ex.Message}");
        }
    }

    // ── SendMessage ────────────────────────────────────────────────────────

    public async Task SendMessage(string text)
    {
        var pipeline = _sessions.GetPipeline(Context.ConnectionId);
        if (pipeline is null)
        {
            await Clients.Caller.SendAsync("Error", "No agent selected. Call SelectAgent first.");
            return;
        }

        _log.LogInformation("[Hub] {Id} → {Preview}",
            Context.ConnectionId, text.Length > 60 ? text[..60] + "…" : text);

        try
        {
            // Signal start
            await Clients.Caller.SendAsync("TokenReceived", "");

            var fullReply = await pipeline.ChatAsync(text, Context.ConnectionAborted);

            // Simulate word-by-word streaming — replace with true streaming
            // once DiagnosticAgent exposes IAsyncEnumerable<string>
            foreach (var word in fullReply.Split(' '))
            {
                await Clients.Caller.SendAsync("TokenReceived", word + " ");
                await Task.Delay(8, Context.ConnectionAborted);
            }

            await Clients.Caller.SendAsync("MessageComplete", fullReply);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Hub] SendMessage failed");
            await Clients.Caller.SendAsync("Error", $"Error: {ex.Message}");
        }
    }

    // ── GetVerdict ─────────────────────────────────────────────────────────

    public async Task GetVerdict()
    {
        var pipeline = _sessions.GetPipeline(Context.ConnectionId);
        if (pipeline is null)
        {
            await Clients.Caller.SendAsync("Error", "No agent selected.");
            return;
        }

        if (!pipeline.CloudAvailable)
        {
            await Clients.Caller.SendAsync("Error",
                "Cloud model not configured. Set OPENAI_API_KEY.");
            return;
        }

        _log.LogInformation("[Hub] {Id} requesting verdict", Context.ConnectionId);

        try
        {
            var result = await pipeline.GetVerdictAsync(Context.ConnectionAborted);
            var text = result?.VerdictText ?? "(no verdict)";
            await Clients.Caller.SendAsync("VerdictComplete", text);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Hub] GetVerdict failed");
            await Clients.Caller.SendAsync("Error", $"Cloud error: {ex.Message}");
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

    public async Task ResetConversation()
    {
        _sessions.GetPipeline(Context.ConnectionId)?.Reset();
        await Clients.Caller.SendAsync("ConversationReset");
    }

    // ── GetRagStatus ───────────────────────────────────────────────────────

    public async Task GetRagStatus()
    {
        var s = _sessions.GetRagStatus(Context.ConnectionId);
        await Clients.Caller.SendAsync("RagStatus", s.HasIndex, s.ChunkCount, s.Details);
    }

    // ── ReindexDocuments ───────────────────────────────────────────────────

    public async Task ReindexDocuments()
    {
        if (_sessions.GetAgentType(Context.ConnectionId) is null)
        {
            await Clients.Caller.SendAsync("Error", "No agent selected.");
            return;
        }

        await Clients.Caller.SendAsync("RagIndexing", "Re-indexing in background…");

        try
        {
            var result = await _sessions.ReindexAsync(
                Context.ConnectionId, Context.ConnectionAborted);

            // Immediately acknowledge start; the continuation will send the final RagIndexed
            await Clients.Caller.SendAsync("RagIndexing", result.Message);

            var pipeline = _sessions.GetPipeline(Context.ConnectionId);
            if (pipeline is not null && pipeline.IsIndexing)
                AttachIndexingContinuation(pipeline, Context.ConnectionId);
            else
                await Clients.Caller.SendAsync("RagIndexed", true, "Index already up to date.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Hub] ReindexDocuments failed");
            await Clients.Caller.SendAsync("Error", $"Indexing failed: {ex.Message}");
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private void AttachIndexingContinuation(HybridAgent.Core.HybridPipeline pipeline, string connectionId)
    {
        var hubContext = _hubContext;
        var log = _log;

        _ = pipeline.IndexingTask.ContinueWith(async t =>
        {
            var client = hubContext.Clients.Client(connectionId);
            if (t.IsCanceled)
            {
                // Client disconnected — don't push anything
                return;
            }
            if (t.IsFaulted)
            {
                log.LogError(t.Exception, "[Hub] Background indexing failed for {ConnId}", connectionId);
                await client.SendAsync("RagIndexed", false,
                    $"Indexing failed: {t.Exception?.InnerException?.Message ?? t.Exception?.Message}");
                return;
            }
            await client.SendAsync("RagIndexed", true,
                $"Index ready — {pipeline.IndexedChunks} chunks");
        }, TaskScheduler.Default);
    }
}