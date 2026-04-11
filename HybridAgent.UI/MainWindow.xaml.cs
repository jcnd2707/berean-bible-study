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
using HybridAgent;
using HybridAgent.Agents;
using HybridAgent.Models;
using Microsoft.Extensions.Logging;

namespace HybridAgent
{
    public partial class MainWindow : Window
    {
        // ── State ─────────────────────────────────────────────────────────
        private HybridPipeline? _pipeline;
        private AgentConfig? _config;
        private bool _busy;
        private int _messageCount;

        // ── Typing indicator state ─────────────────────────────────────────
        private Border? _typingBubble;
        private TextBlock? _timerLabel;
        private Ellipse[]? _dots;
        private DispatcherTimer? _dotTimer;
        private DispatcherTimer? _elapsedTimer;
        private Stopwatch _stopwatch = new();
        private int _dotFrame;

        // Maps radio → choice string identical to the console "1/2/3" switch
        private string CurrentChoice =>
            RbCar.IsChecked == true ? "1" :
            RbBible.IsChecked == true ? "2" :
            RbCSharp.IsChecked == true ? "3" : "1";

        // ── Constructor ───────────────────────────────────────────────────
        public MainWindow()
        {
            InitializeComponent();
            Loaded += async (_, _) => await InitAgentAsync();
        }

        // ── Agent initialisation ──────────────────────────────────────────
        private async Task InitAgentAsync()
        {
            SetUiEnabled(false);
            SetInitBadge("⬤  Initializing...", "#FBBF24");
            ChatPanel.Children.Clear();
            _messageCount = 0;
            TxtMessageCount.Text = "0 messages";

            var choice = CurrentChoice;

            var (config, tools) = choice switch
            {
                "1" => AgentFactory.CreateCarAgent(),
                "2" => AgentFactory.CreateBibleAgent(),
                "3" => AgentFactory.CreateCSharpAgent(),
                _ => AgentFactory.CreateCarAgent()
            };

            config.RagDocsDirectory = choice switch
            {
                "1" => "docs/car",
                "2" => @"D:\Bible Study\bible-docs",
                "3" => "docs/csharp",
                _ => "docs/car"
            };
            config.RagIndexPath = choice switch
            {
                "1" => "index/car.json",
                "2" => @"D:\Bible Study\index\bible.json",
                "3" => "index/csharp.json",
                _ => "index/car.json"
            };

            System.IO.Directory.CreateDirectory(config.RagDocsDirectory);
            System.IO.Directory.CreateDirectory("index");

            _config = config;

            string agentName = choice switch
            {
                "1" => "Car Diagnostics",
                "2" => "Bible Research",
                "3" => "C# Troubleshooting",
                _ => "Car Diagnostics"
            };
            TxtAgentTitle.Text = agentName;
            TxtModelBadge.Text = config.OllamaModel;
            TxtDocsPath.Text = config.RagDocsDirectory;

            if (!string.IsNullOrWhiteSpace(config.OpenAiApiKey))
            {
                TxtCloudStatus.Text = config.CloudModel;
                TxtCloudStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
            }
            else
            {
                TxtCloudStatus.Text = "not configured";
                TxtCloudStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
            }

            _pipeline = null;

            try
            {
                var logFactory = LoggerFactory.Create(b => b
                    .AddConsole()
                    .SetMinimumLevel(LogLevel.Information));

                _pipeline = await HybridPipeline.CreateAsync(config, tools, logFactory);

                SetInitBadge("⬤  Ready", "#4ADE80");
                SetUiEnabled(true);
                BtnVerdict.IsEnabled = _pipeline.CloudAvailable;

                AddSystemMessage($"Agent '{agentName}' loaded. " +
                    (_pipeline.CloudAvailable
                        ? $"Cloud ({config.CloudModel}) available."
                        : "Cloud not configured."));
            }
            catch (Exception ex)
            {
                SetInitBadge("⬤  Error", "#F87171");
                AddSystemMessage($"[Error] {ex.GetType().Name}: {ex.Message}");

                if (ex.Message.Contains("connect", StringComparison.OrdinalIgnoreCase))
                    AddSystemMessage("→ Is Ollama running?  Try: ollama serve");
            }
        }

        // ── Event: agent radio changed ────────────────────────────────────
        private async void AgentRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            await InitAgentAsync();
        }

        // ── Events: send ──────────────────────────────────────────────────
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

        // ── Event: Verdict ────────────────────────────────────────────────
        private async void BtnVerdict_Click(object sender, RoutedEventArgs e)
        {
            if (_pipeline is null || _busy) return;

            SetBusy(true);
            ShowTypingIndicator(isVerdict: true);

            try
            {
                var result = await _pipeline.GetVerdictAsync();
                HideTypingIndicator();
                if (result is not null)
                    AddAgentBubble(FormatVerdict(result), isVerdict: true);
            }
            catch (Exception ex)
            {
                HideTypingIndicator();
                AddSystemMessage($"[Cloud error] {ex.Message}");
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ── Event: Reset ──────────────────────────────────────────────────
        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            _pipeline?.Reset();
            ChatPanel.Children.Clear();
            _messageCount = 0;
            TxtMessageCount.Text = "0 messages";
        }

        // ── Core chat ─────────────────────────────────────────────────────
        private async Task SendMessageAsync()
        {
            var text = InputBox.Text.Trim();
            if (string.IsNullOrEmpty(text) || _pipeline is null || _busy) return;

            InputBox.Clear();
            AddUserBubble(text);
            SetBusy(true);
            ShowTypingIndicator();

            try
            {
                var reply = await _pipeline.ChatAsync(text);
                HideTypingIndicator();
                AddAgentBubble(reply ?? "(no response)");
            }
            catch (Exception ex)
            {
                HideTypingIndicator();
                AddSystemMessage($"[Error] {ex.GetType().Name}: {ex.Message}");

                if (ex.Message.Contains("connect", StringComparison.OrdinalIgnoreCase))
                    AddSystemMessage("→ Is Ollama running?  Try: ollama serve");
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  TYPING INDICATOR
        // ════════════════════════════════════════════════════════════════════

        private void ShowTypingIndicator(bool isVerdict = false)
        {
            // Outer wrapper — left-aligned like agent bubbles
            var outer = new Border
            {
                Margin = new Thickness(0, 8, 60, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Tag = "typing"
            };

            var stack = new StackPanel();

            // Agent name label
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

            // Bubble shell
            var bubble = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x20)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x3E)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3, 10, 10, 10),
                Padding = new Thickness(16, 12, 20, 12)
            };

            var innerStack = new StackPanel();

            // Three animated dots
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

            // Elapsed time label
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

            // Start dot animation — 400 ms per frame
            _dotFrame = 0;
            _dotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _dotTimer.Tick += (_, _) => AnimateDots();
            _dotTimer.Start();
            AnimateDots(); // fire immediately so dots appear at once

            // Start elapsed timer — ticks every second
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
                double target = (i == active) ? 1.0 : 0.2;
                _dots[i].BeginAnimation(
                    UIElement.OpacityProperty,
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
            _dotTimer?.Stop();
            _dotTimer = null;

            _elapsedTimer?.Stop();
            _elapsedTimer = null;
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
        //  MESSAGE RENDERERS
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

        // ── Helpers ───────────────────────────────────────────────────────

        private void SetUiEnabled(bool enabled)
        {
            InputBox.IsEnabled = enabled;
            BtnSend.IsEnabled = enabled;
            BtnReset.IsEnabled = enabled;

            if (enabled && _pipeline is not null)
                BtnVerdict.IsEnabled = _pipeline.CloudAvailable;
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
            TxtInitStatus.Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
        }

        private void UpdateMessageCount()
        {
            TxtMessageCount.Text =
                $"{_messageCount} message{(_messageCount == 1 ? "" : "s")}";
        }

        private void ScrollToBottom() => ChatScrollViewer.ScrollToEnd();

        private static string FormatVerdict(VerdictResult result)
            => result.ToString() ?? "(no verdict)";
    }
}