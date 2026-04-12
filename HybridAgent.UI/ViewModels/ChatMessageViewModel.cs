using CommunityToolkit.Mvvm.ComponentModel;

namespace HybridAgent.WPF.ViewModels;

public enum MessageRole { User, Agent, System }

/// <summary>
/// Single message in the chat history list.
/// Text is observable so streaming tokens can append to it live.
/// </summary>
public partial class ChatMessageViewModel : ObservableObject
{
    public MessageRole Role { get; init; }

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private bool _isStreaming;

    public bool IsUser => Role == MessageRole.User;
    public bool IsAgent => Role == MessageRole.Agent;
    public bool IsSystem => Role == MessageRole.System;

    public DateTime Timestamp { get; init; } = DateTime.Now;
}