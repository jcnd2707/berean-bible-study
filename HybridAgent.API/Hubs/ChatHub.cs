using HybridAgent.API.Services;
using Microsoft.AspNetCore.SignalR;

namespace HybridAgent.API.Hubs;

/// <summary>
/// SignalR hub — one persistent connection per WPF window.
///
/// Client → Server methods (called by WPF):
///   SelectAgent(agentType)        — switch to car / bible / csharp
///   SendMessage(text)             — send a chat message, receive streamed tokens
///   ResetConversation()           — clear history, start fresh topic
///   GetRagStatus()                — check index status for current agent
///   ReindexDocuments()            — rebuild the RAG index from docs folder
///
/// Server → Client events (received by WPF):
///   AgentSelected(agentType, cloudAvailable, ragChunks)
///   TokenReceived(token)          — one chunk of the streaming response
///   MessageComplete(fullText)     — full assembled response when done
///   RagStatus(hasIndex, chunks, details)
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
        _log.LogInformation("[Hub] Connected: {ConnId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _sessions.RemoveSession(Context.ConnectionId);
        _log.LogInformation("[Hub] Disconnected: {ConnId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    // ── Client → Server ────────────────────────────────────────────────────

    /// <summary>Select or switch the active agent for this connection.</summary>
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
                agentType,
                pipeline.CloudAvailable,
                status.ChunkCount);

            _log.LogInformation("[Hub] {ConnId} agent={Agent} cloud={Cloud} rag={Chunks}",
                Context.ConnectionId, agentType, pipeline.CloudAvailable, status.ChunkCount);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Hub] SelectAgent failed");
            await Clients.Caller.SendAsync("Error", $"Failed to load agent: {ex.Message}");
        }
    }

    /// <summary>
    /// Send a chat message. The response is streamed token-by-token via TokenReceived,
    /// then a final MessageComplete event carries the full assembled text.
    ///
    /// Note: HybridPipeline.ChatAsync() returns the full string, not a stream.
    /// We simulate streaming by splitting on word boundaries client-side.
    /// To get true token streaming, wire OllamaSharp's streaming API into
    /// DiagnosticAgent and yield each token via IAsyncEnumerable.
    /// </summary>
    public async Task SendMessage(string text)
    {
        var pipeline = _sessions.GetPipeline(Context.ConnectionId);
        if (pipeline is null)
        {
            await Clients.Caller.SendAsync("Error",
                "No agent selected. Call SelectAgent first.");
            return;
        }

        _log.LogInformation("[Hub] {ConnId} message: {Text}", Context.ConnectionId,
            text.Length > 60 ? text[..60] + "..." : text);

        try
        {
            // Signal that processing has started
            await Clients.Caller.SendAsync("TokenReceived", "");

            var fullReply = await pipeline.ChatAsync(text, Context.ConnectionAborted);

            // Simulate streaming — send words one by one so the WPF UI
            // can animate the response appearing progressively.
            // Replace this with true streaming once DiagnosticAgent supports it.
            var words = fullReply.Split(' ');
            foreach (var word in words)
            {
                await Clients.Caller.SendAsync("TokenReceived", word + " ");
                await Task.Delay(10, Context.ConnectionAborted); // pacing
            }

            // Final event — WPF uses this to commit the full message to history
            await Clients.Caller.SendAsync("MessageComplete", fullReply);
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("[Hub] {ConnId} message cancelled", Context.ConnectionId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Hub] SendMessage failed");
            await Clients.Caller.SendAsync("Error", $"Error: {ex.Message}");
        }
    }

    /// <summary>Clear conversation history for this connection.</summary>
    public async Task ResetConversation()
    {
        _sessions.GetPipeline(Context.ConnectionId)?.Reset();
        await Clients.Caller.SendAsync("ConversationReset");
        _log.LogInformation("[Hub] {ConnId} conversation reset", Context.ConnectionId);
    }

    /// <summary>Return the RAG index status for the current agent.</summary>
    public async Task GetRagStatus()
    {
        var status = _sessions.GetRagStatus(Context.ConnectionId);
        await Clients.Caller.SendAsync("RagStatus",
            status.HasIndex, status.ChunkCount, status.Details);
    }

    /// <summary>
    /// Rebuild the RAG index from the docs folder.
    /// This can take a while — the WPF UI should show a progress indicator.
    /// </summary>
    public async Task ReindexDocuments()
    {
        var agentType = _sessions.GetAgentType(Context.ConnectionId);
        if (agentType is null)
        {
            await Clients.Caller.SendAsync("Error", "No agent selected.");
            return;
        }

        await Clients.Caller.SendAsync("RagIndexing", "Indexing documents...");

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