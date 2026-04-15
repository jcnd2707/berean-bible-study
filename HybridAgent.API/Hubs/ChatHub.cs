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
    private readonly ILogger<ChatHub> _log;

    public ChatHub(AgentSessionService sessions, ILogger<ChatHub> log)
    {
        _sessions = sessions;
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

        await Clients.Caller.SendAsync("RagIndexing", "Indexing documents…");

        try
        {
            var result = await _sessions.ReindexAsync(
                Context.ConnectionId, Context.ConnectionAborted);

            await Clients.Caller.SendAsync("RagIndexed", result.Success, result.Message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Hub] ReindexDocuments failed");
            await Clients.Caller.SendAsync("Error", $"Indexing failed: {ex.Message}");
        }
    }
}