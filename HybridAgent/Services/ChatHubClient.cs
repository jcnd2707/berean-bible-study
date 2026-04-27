using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace HybridAgent.Core.Services;

/// <summary>
/// Wraps the SignalR connection to the API hub.
/// Lives in Core so any client (WPF, Blazor, MAUI) can share it.
///
/// Server → Client events:
///   TokenReceived(token)                  — streaming token
///   MessageComplete(fullText)             — full response when done
///   AgentSelected(type, cloudAvailable, ragChunks)
///   RagStatus(hasIndex, chunks, details)
///   RagIndexed(success, message)
///   RagIndexing(progressMessage)
///   ConversationReset
///   VerdictComplete(verdictText)
///   Error(message)
///   ConnectionStateChanged(state)
/// </summary>
public class ChatHubClient : IAsyncDisposable
{
    private readonly HubConnection _hub;

    // ── Events ─────────────────────────────────────────────────────────────
    public event Action<string>? TokenReceived;
    public event Action<string>? MessageComplete;
    public event Action<string, bool, int>? AgentSelected;      // type, cloudAvailable, ragChunks
    public event Action<bool, int, string>? RagStatus;          // hasIndex, chunks, details
    public event Action<bool, string>? RagIndexed;         // success, message
    public event Action<string>? RagIndexing;        // progress text
    public event Action? ConversationReset;
    public event Action<string>? VerdictComplete;    // verdict text
    public event Action<string>? ErrorReceived;
    public event Action<HubConnectionState>? ConnectionStateChanged;

    public HubConnectionState State => _hub.State;
    public bool IsConnected => _hub.State == HubConnectionState.Connected;

    public ChatHubClient(string hubUrl = "http://localhost:5050/hubs/chat")
    {
        _hub = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10)
            })
            .ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning))
            .Build();

        RegisterHandlers();

        _hub.Reconnecting += _ => { ConnectionStateChanged?.Invoke(_hub.State); return Task.CompletedTask; };
        _hub.Reconnected += _ => { ConnectionStateChanged?.Invoke(_hub.State); return Task.CompletedTask; };
        _hub.Closed += _ => { ConnectionStateChanged?.Invoke(_hub.State); return Task.CompletedTask; };
    }

    // ── Connection ─────────────────────────────────────────────────────────

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_hub.State == HubConnectionState.Disconnected)
        {
            await _hub.StartAsync(ct);
            ConnectionStateChanged?.Invoke(_hub.State);
        }
    }

    public async Task DisconnectAsync()
    {
        await _hub.StopAsync();
        ConnectionStateChanged?.Invoke(_hub.State);
    }

    // ── Hub method calls ───────────────────────────────────────────────────

    public Task SelectAgentAsync(string agentType) =>
        _hub.SendAsync("SelectAgent", agentType);

    public Task SendMessageAsync(string text, string mode = "Deep") =>
        _hub.SendAsync("SendMessage", text, mode);

    public Task GetVerdictAsync() =>
        _hub.SendAsync("GetVerdict");

    public Task ResetConversationAsync() =>
        _hub.SendAsync("ResetConversation");

    public Task GetRagStatusAsync() =>
        _hub.SendAsync("GetRagStatus");

    public Task ReindexDocumentsAsync() =>
        _hub.SendAsync("ReindexDocuments");

    // ── Server → Client handlers ───────────────────────────────────────────

    private void RegisterHandlers()
    {
        _hub.On<string>("TokenReceived",
            t => TokenReceived?.Invoke(t));

        _hub.On<string>("MessageComplete",
            t => MessageComplete?.Invoke(t));

        _hub.On<string, bool, int>("AgentSelected",
            (type, cloud, chunks) => AgentSelected?.Invoke(type, cloud, chunks));

        _hub.On<bool, int, string>("RagStatus",
            (has, n, d) => RagStatus?.Invoke(has, n, d));

        _hub.On<bool, string>("RagIndexed",
            (ok, msg) => RagIndexed?.Invoke(ok, msg));

        _hub.On<string>("RagIndexing",
            msg => RagIndexing?.Invoke(msg));

        _hub.On("ConversationReset",
            () => ConversationReset?.Invoke());

        _hub.On<string>("VerdictComplete",
            t => VerdictComplete?.Invoke(t));

        _hub.On<string>("Error",
            msg => ErrorReceived?.Invoke(msg));
    }

    public async ValueTask DisposeAsync() =>
        await _hub.DisposeAsync();
}