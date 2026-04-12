using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HybridAgent.Core.Services;
using HybridAgent.WPF.ViewModels;
using Microsoft.AspNetCore.SignalR.Client;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace HybridAgent.ViewModels;

public partial class ChatViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ChatHubClient _hub;
    private readonly Dispatcher _dispatcher;

    // Tracks the in-progress streaming message so tokens can append to it
    private ChatMessageViewModel? _streamingMessage;

    // ── Observable properties ──────────────────────────────────────────────

    [ObservableProperty] private string _inputText = string.Empty;
    [ObservableProperty] private string _selectedAgent = "Car";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "Disconnected";
    [ObservableProperty] private bool _cloudAvailable;
    [ObservableProperty] private int _ragChunkCount;
    [ObservableProperty] private bool _ragHasIndex;
    [ObservableProperty] private bool _isIndexing;
    [ObservableProperty] private string _connectionStatus = "Disconnected";

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];

    public string[] AvailableAgents { get; } = ["Car", "Bible", "CSharp"];

    // ── Constructor ────────────────────────────────────────────────────────

    public ChatViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;
        _hub = new ChatHubClient();

        WireHubEvents();
    }

    // ── Commands ───────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ConnectAsync()
    {
        try
        {
            StatusText = "Connecting...";
            await _hub.ConnectAsync();
            await _hub.SelectAgentAsync(SelectedAgent);
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Connection failed: {ex.Message}");
            StatusText = "Connection failed";
        }
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = InputText.Trim();
        if (string.IsNullOrEmpty(text)) return;

        InputText = string.Empty;
        IsBusy = true;

        AddUserMessage(text);
        StartStreamingMessage();

        await _hub.SendMessageAsync(text);
    }

    private bool CanSend() => IsConnected && !IsBusy && !string.IsNullOrWhiteSpace(InputText);

    [RelayCommand]
    private async Task SelectAgentAsync()
    {
        if (!IsConnected) return;
        IsBusy = true;
        StatusText = $"Loading {SelectedAgent} agent...";
        await _hub.SelectAgentAsync(SelectedAgent);
    }

    [RelayCommand]
    private async Task ResetConversationAsync()
    {
        if (!IsConnected) return;
        await _hub.ResetConversationAsync();
    }

    [RelayCommand]
    private async Task ReindexAsync()
    {
        if (!IsConnected || IsIndexing) return;
        IsIndexing = true;
        StatusText = "Indexing documents...";
        await _hub.ReindexDocumentsAsync();
    }

    [RelayCommand]
    private async Task RefreshRagStatusAsync()
    {
        if (!IsConnected) return;
        await _hub.GetRagStatusAsync();
    }

    // ── Hub event wiring ───────────────────────────────────────────────────

    private void WireHubEvents()
    {
        _hub.ConnectionStateChanged += state => Dispatch(() =>
        {
            IsConnected = state == HubConnectionState.Connected;
            ConnectionStatus = state.ToString();
            StatusText = IsConnected ? $"{SelectedAgent} agent ready" : state.ToString();
            SendCommand.NotifyCanExecuteChanged();
        });

        _hub.AgentSelected += (type, cloud, chunks) => Dispatch(() =>
        {
            SelectedAgent = type;
            CloudAvailable = cloud;
            RagChunkCount = chunks;
            RagHasIndex = chunks > 0;
            IsBusy = false;
            StatusText = $"{type} agent ready" +
                              (cloud ? " · cloud enabled" : "") +
                              (chunks > 0 ? $" · {chunks} RAG chunks" : "");
            AddSystemMessage($"Switched to {type} agent." +
                             (cloud ? " Cloud verdict available." : "") +
                             (chunks > 0 ? $" {chunks} document chunks indexed." : " No RAG index."));
        });

        _hub.TokenReceived += token => Dispatch(() =>
        {
            if (_streamingMessage is not null && !string.IsNullOrEmpty(token))
                _streamingMessage.Text += token;
        });

        _hub.MessageComplete += fullText => Dispatch(() =>
        {
            // Finalize the streaming message with the authoritative full text
            if (_streamingMessage is not null)
            {
                _streamingMessage.Text = fullText;
                _streamingMessage.IsStreaming = false;
                _streamingMessage = null;
            }
            IsBusy = false;
            SendCommand.NotifyCanExecuteChanged();
        });

        _hub.ConversationReset += () => Dispatch(() =>
        {
            Messages.Clear();
            AddSystemMessage("Conversation reset. Starting a new topic.");
        });

        _hub.RagStatus += (hasIndex, chunks, details) => Dispatch(() =>
        {
            RagHasIndex = hasIndex;
            RagChunkCount = chunks;
            AddSystemMessage($"RAG: {(hasIndex ? $"{chunks} chunks indexed" : "no index")} — {details}");
        });

        _hub.RagIndexing += msg => Dispatch(() =>
        {
            StatusText = msg;
            AddSystemMessage(msg);
        });

        _hub.RagIndexed += (success, message) => Dispatch(() =>
        {
            IsIndexing = false;
            StatusText = success ? "Indexing complete" : "Indexing failed";
            AddSystemMessage(message);
            _ = _hub.GetRagStatusAsync();
        });

        _hub.ErrorReceived += msg => Dispatch(() =>
        {
            IsBusy = false;
            IsIndexing = false;
            AddSystemMessage($"Error: {msg}");
            SendCommand.NotifyCanExecuteChanged();

            // Clean up any incomplete streaming message
            if (_streamingMessage is not null)
            {
                _streamingMessage.IsStreaming = false;
                _streamingMessage = null;
            }
        });
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private void AddUserMessage(string text) =>
        Messages.Add(new ChatMessageViewModel { Role = MessageRole.User, Text = text });

    private void AddSystemMessage(string text) =>
        Messages.Add(new ChatMessageViewModel { Role = MessageRole.System, Text = text });

    private void StartStreamingMessage()
    {
        _streamingMessage = new ChatMessageViewModel
        {
            Role = MessageRole.Agent,
            IsStreaming = true,
            Text = string.Empty
        };
        Messages.Add(_streamingMessage);
    }

    private void Dispatch(Action action) =>
        _dispatcher.Invoke(action);

    public async ValueTask DisposeAsync() =>
        await _hub.DisposeAsync();
}