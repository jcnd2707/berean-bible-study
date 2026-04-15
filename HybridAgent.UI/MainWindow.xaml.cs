using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using HybridAgent.Core.Services;
using Microsoft.AspNetCore.SignalR.Client;

namespace HybridAgent
{
    public partial class MainWindow : Window
    {
        // ── Hub client (replaces direct _pipeline calls) ───────────────────
        private readonly ChatHubClient _hub = new("http://localhost:5050/hubs/chat");

        // ── UI state ───────────────────────────────────────────────────────
        private bool _busy;
        private int _messageCount;

        // ── Streaming state ────────────────────────────────────────────────
        // The TextBlock inside the current agent bubble being streamed into
        private TextBlock? _streamingTextBlock;

        // ── Typing indicator state ─────────────────────────────────────────
        private Border? _typingBubble;
        private TextBlock? _timerLabel;
        private Ellipse[]? _dots;
        private DispatcherTimer? _dotTimer;
        private DispatcherTimer? _elapsedTimer;
        private Stopwatch _stopwatch = new();
        private int _dotFrame;

        // Maps radio → agent type string matching AgentType enum
        private string CurrentAgentType =>
            RbCar.IsChecked == true ? "Car" :
            RbBible.IsChecked == true ? "Bible" :
            RbCSharp.IsChecked == true ? "CSharp" : "Car";

        // ── Constructor ────────────────────────────────────────────────────
        public MainWindow()
        {
            InitializeComponent();
            WireHubEvents();
            Loaded += async (_, _) => await ConnectAndSelectAsync();
            Closing += async (_, e) =>
            {
                e.Cancel = false;
                await _hub.DisposeAsync();
            };
        }

        // ── Connect + select initial agent ─────────────────────────────────
        private async Task ConnectAndSelectAsync()
        {
            SetUiEnabled(false);
            SetInitBadge("⬤  Connecting…", "#FBBF24");
            ChatPanel.Children.Clear();
            _messageCount = 0;
            TxtMessageCount.Text = "0 messages";

            try
            {
                await _hub.ConnectAsync();
                await _hub.SelectAgentAsync(CurrentAgentType);
            }
            catch (Exception ex)
            {
                SetInitBadge("⬤  Connection failed", "#F87171");
                AddSystemMessage($"[Error] {ex.Message}");
                AddSystemMessage("→ Is HybridAgent.API running?  dotnet run in the API project.");
            }
        }

        // ── Hub event wiring ───────────────────────────────────────────────
        private void WireHubEvents()
        {
            _hub.ConnectionStateChanged += state => Dispatch(() =>
            {
                if (state == HubConnectionState.Reconnecting)
                {
                    SetInitBadge("⬤  Reconnecting…", "#FBBF24");
                    SetUiEnabled(false);
                    AddSystemMessage("Connection lost — reconnecting…");
                }
                else if (state == HubConnectionState.Disconnected)
                {
                    SetInitBadge("⬤  Disconnected", "#F87171");
                    SetUiEnabled(false);
                }
            });

            // AgentSelected — fired after SelectAgent completes on the server
            _hub.AgentSelected += (type, cloudAvailable, ragChunks) => Dispatch(() =>
            {
                // Update header badges to match what the server loaded
                string agentName = type switch
                {
                    "Car" => "Car Diagnostics",
                    "Bible" => "Bible Research",
                    "CSharp" => "C# Troubleshooting",
                    _ => type
                };
                string modelName = type switch
                {
                    "Car" => "llama3.2:3b",
                    "Bible" => "llama3.2:3b",
                    "CSharp" => "deepseek-coder:6.7b",
                    _ => type
                };

                TxtAgentTitle.Text = agentName;
                TxtModelBadge.Text = modelName;
                TxtDocsPath.Text = type switch
                {
                    "Car" => "docs/car",
                    "Bible" => "D:\\Bible Study\\bible-docs\\index",
                    "CSharp" => "docs/csharp",
                    _ => "docs/" + type.ToLower()
                };

                if (cloudAvailable)
                {
                    TxtCloudStatus.Text = "gpt-4o";
                    TxtCloudStatus.Foreground =
                        new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
                }
                else
                {
                    TxtCloudStatus.Text = "not configured";
                    TxtCloudStatus.Foreground =
                        new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
                }

                BtnVerdict.IsEnabled = cloudAvailable;
                SetInitBadge("⬤  Ready", "#4ADE80");
                SetUiEnabled(true);

                AddSystemMessage($"Agent '{agentName}' loaded." +
                    (cloudAvailable ? " Cloud verdict available." : "") +
                    (ragChunks > 0 ? $" {ragChunks} RAG chunks indexed." : " No RAG index."));
            });

            // Streaming — tokens append to the current streaming bubble
            _hub.TokenReceived += token => Dispatch(() =>
            {
                if (_streamingTextBlock is not null && !string.IsNullOrEmpty(token))
                    _streamingTextBlock.Text += token;
            });

            // Message done — hide typing indicator, commit final text
            _hub.MessageComplete += fullText => Dispatch(() =>
            {
                HideTypingIndicator();

                // Replace the partial streamed text with the authoritative full reply
                if (_streamingTextBlock is not null)
                {
                    _streamingTextBlock.Text = fullText;
                    _streamingTextBlock = null;
                }
                else
                {
                    // Fallback: no streaming bubble was created yet
                    AddAgentBubble(fullText);
                }

                SetBusy(false);
            });

            // Verdict done
            _hub.VerdictComplete += verdictText => Dispatch(() =>
            {
                HideTypingIndicator();
                AddAgentBubble(verdictText, isVerdict: true);
                SetBusy(false);
            });

            // Conversation reset
            _hub.ConversationReset += () => Dispatch(() =>
            {
                ChatPanel.Children.Clear();
                _messageCount = 0;
                TxtMessageCount.Text = "0 messages";
            });

            // RAG events — surface as system messages
            _hub.RagStatus += (hasIndex, chunks, details) => Dispatch(() =>
                AddSystemMessage($"RAG: {(hasIndex ? $"{chunks} chunks" : "no index")} — {details}"));

            _hub.RagIndexing += msg => Dispatch(() =>
                AddSystemMessage(msg));

            _hub.RagIndexed += (success, message) => Dispatch(() =>
                AddSystemMessage(message));

            // Errors
            _hub.ErrorReceived += msg => Dispatch(() =>
            {
                HideTypingIndicator();
                AddSystemMessage($"[Error] {msg}");
                SetBusy(false);
            });
        }

        // ── Agent radio changed ────────────────────────────────────────────
        private async void AgentRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || !_hub.IsConnected) return;

            SetUiEnabled(false);
            SetInitBadge("⬤  Switching agent…", "#FBBF24");
            ChatPanel.Children.Clear();
            _messageCount = 0;
            TxtMessageCount.Text = "0 messages";

            await _hub.SelectAgentAsync(CurrentAgentType);
            // UI update happens in AgentSelected handler above
        }

        // ── Send ───────────────────────────────────────────────────────────
        private async void BtnSend_Click(object sender, RoutedEventArgs e)
            => await SendMessageAsync();

        private async void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !Keyboard.IsKeyDown(Key.LeftShift))
            {
                e.Handled = true;
                await SendMessageAsync();
            }
        }

        private async Task SendMessageAsync()
        {
            var text = InputBox.Text.Trim();
            if (string.IsNullOrEmpty(text) || _busy || !_hub.IsConnected) return;

            InputBox.Clear();
            AddUserBubble(text);
            SetBusy(true);

            // Create the agent bubble now so tokens can stream into it
            ShowTypingIndicator();
            _streamingTextBlock = PrepareStreamingBubble();

            await _hub.SendMessageAsync(text);
            // Rest handled in TokenReceived / MessageComplete
        }

        // ── Verdict ────────────────────────────────────────────────────────
        private async void BtnVerdict_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || !_hub.IsConnected) return;

            SetBusy(true);
            ShowTypingIndicator(isVerdict: true);
            await _hub.GetVerdictAsync();
            // Response handled in VerdictComplete handler
        }

        // ── Reset ──────────────────────────────────────────────────────────
        private async void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            if (!_hub.IsConnected) return;
            await _hub.ResetConversationAsync();
            // UI clear handled in ConversationReset handler
        }

        // ════════════════════════════════════════════════════════════════════
        //  STREAMING BUBBLE
        //  Creates the agent bubble shell and returns the TextBlock to stream into.
        // ════════════════════════════════════════════════════════════════════

        private TextBlock PrepareStreamingBubble()
        {
            _messageCount++;
            UpdateMessageCount();

            var outer = new Border
            {
                Margin = new Thickness(0, 8, 60, 8),
                HorizontalAlignment = HorizontalAlignment.Left
            };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = TxtAgentTitle.Text,
                FontSize = 10,
                FontFamily = new FontFamily("Consolas"),
                Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF)),
                Margin = new Thickness(4, 0, 0, 4)
            });

            var textBlock = new TextBlock
            {
                Text = "",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                LineHeight = 20
            };

            var bubble = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x20)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3E)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3, 10, 10, 10),
                Padding = new Thickness(14, 10, 14, 10),
                Child = textBlock
            };

            stack.Children.Add(bubble);
            outer.Child = stack;
            ChatPanel.Children.Add(outer);
            ScrollToBottom();

            return textBlock;
        }

        // ════════════════════════════════════════════════════════════════════
        //  TYPING INDICATOR  (unchanged from your original)
        // ════════════════════════════════════════════════════════════════════

        private void ShowTypingIndicator(bool isVerdict = false)
        {
            var outer = new Border
            {
                Margin = new Thickness(0, 8, 60, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Tag = "typing"
            };

            var stack = new StackPanel();
            var labelText = TxtAgentTitle.Text + (isVerdict ? "  ⚡ cloud" : "");
            stack.Children.Add(new TextBlock
            {
                Text = labelText,
                FontSize = 10,
                FontFamily = new FontFamily("Consolas"),
                Foreground = isVerdict
                    ? new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24))
                    : new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF)),
                Margin = new Thickness(4, 0, 0, 4)
            });

            var bubble = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x20)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3E)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3, 10, 10, 10),
                Padding = new Thickness(16, 12, 20, 12)
            };

            var innerStack = new StackPanel();
            var dotRow = new StackPanel { Orientation = Orientation.Horizontal };

            _dots = new Ellipse[3];
            for (int i = 0; i < 3; i++)
            {
                var dot = new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Margin = new Thickness(i == 0 ? 0 : 6, 0, 0, 0),
                    Fill = new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF)),
                    Opacity = 0.2
                };
                _dots[i] = dot;
                dotRow.Children.Add(dot);
            }

            _timerLabel = new TextBlock
            {
                Text = "thinking…",
                FontSize = 10,
                FontFamily = new FontFamily("Consolas"),
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x5A)),
                Margin = new Thickness(0, 7, 0, 0)
            };

            innerStack.Children.Add(dotRow);
            innerStack.Children.Add(_timerLabel);
            bubble.Child = innerStack;
            stack.Children.Add(bubble);
            outer.Child = stack;

            _typingBubble = outer;
            ChatPanel.Children.Add(outer);
            ScrollToBottom();

            _dotFrame = 0;
            _dotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _dotTimer.Tick += (_, _) => AnimateDots();
            _dotTimer.Start();
            AnimateDots();

            _stopwatch.Restart();
            _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _elapsedTimer.Tick += (_, _) => UpdateElapsed();
            _elapsedTimer.Start();
        }

        private void AnimateDots()
        {
            if (_dots is null) return;
            int active = _dotFrame % 3;
            for (int i = 0; i < 3; i++)
            {
                double target = i == active ? 1.0 : 0.2;
                _dots[i].BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(target, TimeSpan.FromMilliseconds(250))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
                    });
            }
            _dotFrame++;
        }

        private void UpdateElapsed()
        {
            if (_timerLabel is null) return;
            int s = (int)_stopwatch.Elapsed.TotalSeconds;
            _timerLabel.Text = s < 60
                ? $"thinking for {s}s…"
                : $"thinking for {s / 60}m {s % 60}s…";
        }

        private void HideTypingIndicator()
        {
            _dotTimer?.Stop(); _dotTimer = null;
            _elapsedTimer?.Stop(); _elapsedTimer = null;
            _stopwatch.Stop();

            if (_typingBubble is not null)
            {
                ChatPanel.Children.Remove(_typingBubble);
                _typingBubble = null;
            }
            _dots = null;
            _timerLabel = null;
        }

        // ════════════════════════════════════════════════════════════════════
        //  MESSAGE RENDERERS  (unchanged from your original)
        // ════════════════════════════════════════════════════════════════════

        private void AddUserBubble(string text)
        {
            _messageCount++;
            UpdateMessageCount();

            var outer = new Border
            {
                Margin = new Thickness(60, 8, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = "You",
                FontSize = 10,
                FontFamily = new FontFamily("Consolas"),
                Foreground = new SolidColorBrush(Color.FromRgb(0x78, 0x78, 0xA0)),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 4, 4)
            });

            var bubble = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x25, 0x40)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF)) { Opacity = 0.4 },
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10, 3, 10, 10),
                Padding = new Thickness(14, 10, 14, 10)
            };
            bubble.Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13
            };

            stack.Children.Add(bubble);
            outer.Child = stack;
            ChatPanel.Children.Add(outer);
            ScrollToBottom();
        }

        private void AddAgentBubble(string text, bool isVerdict = false)
        {
            _messageCount++;
            UpdateMessageCount();

            var outer = new Border
            {
                Margin = new Thickness(0, 8, 60, 8),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            var stack = new StackPanel();
            var agentName = TxtAgentTitle.Text + (isVerdict ? "  ⚡ cloud" : "");

            stack.Children.Add(new TextBlock
            {
                Text = agentName,
                FontSize = 10,
                FontFamily = new FontFamily("Consolas"),
                Foreground = isVerdict
                    ? new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24))
                    : new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF)),
                Margin = new Thickness(4, 0, 0, 4)
            });

            var bubble = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x20)),
                BorderBrush = isVerdict
                    ? new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)) { Opacity = 0.5 }
                    : new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3E)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3, 10, 10, 10),
                Padding = new Thickness(14, 10, 14, 10)
            };
            bubble.Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                LineHeight = 20
            };

            stack.Children.Add(bubble);
            outer.Child = stack;
            ChatPanel.Children.Add(outer);
            ScrollToBottom();
        }

        private void AddSystemMessage(string text)
        {
            ChatPanel.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x5A)),
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch
            });
            ScrollToBottom();
        }

        // ── Helpers ────────────────────────────────────────────────────────

        private void SetUiEnabled(bool enabled)
        {
            InputBox.IsEnabled = enabled;
            BtnSend.IsEnabled = enabled;
            BtnReset.IsEnabled = enabled;
            BtnVerdict.IsEnabled = enabled && _hub.IsConnected;
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            InputBox.IsEnabled = !busy;
            BtnSend.IsEnabled = !busy;
        }

        private void SetInitBadge(string text, string hex)
        {
            TxtInitStatus.Text = text;
            TxtInitStatus.Foreground =
                (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
        }

        private void UpdateMessageCount() =>
            TxtMessageCount.Text =
                $"{_messageCount} message{(_messageCount == 1 ? "" : "s")}";

        private void ScrollToBottom() =>
            ChatScrollViewer.ScrollToEnd();

        private void Dispatch(Action a) =>
            Dispatcher.Invoke(a);
    }
}