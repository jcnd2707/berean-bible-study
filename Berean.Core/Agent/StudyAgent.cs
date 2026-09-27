using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Berean.Core.Agent;

/// <summary>
/// Conversational agent over any <see cref="IChatClient"/> (Ollama, Anthropic, OpenAI, Claude Code).
///
/// _history persists across every call so follow-up questions keep their context ("when did he
/// die?" knows who "he" is). The tool loop is not here: the chat client is wrapped with function
/// invocation, which works with any provider and streams.
///
/// Per turn, the user message is built as
///   [SDA / Compare instructions] + [retrieved material] + "User question: …"
/// so the system prompt never changes (which keeps a conversation and any prompt cache valid) and
/// the material sits before the question rather than being chased by a reminder.
/// </summary>
public class StudyAgent
{
    private readonly LlmClient _llm;
    private readonly ToolRegistry _registry;
    private readonly StudyAgentOptions _config;
    private readonly LlmConfig _llmConfig;
    private readonly ILogger _log;
    private readonly List<ChatMessage> _history = [];
    private bool _initialized;
    private string _conversationId = Guid.NewGuid().ToString("N");

    /// <summary>Raised when the model calls a tool: (name, arguments as JSON).</summary>
    public event Action<string, string>? ToolStarted;

    /// <summary>
    /// Identifies the conversation (and, for Claude Code, keys the CLI session that mirrors it).
    /// Set it to the persisted conversation's id so a reopened conversation finds its session.
    /// </summary>
    public string ConversationId
    {
        get => _conversationId;
        set => _conversationId = value;
    }

    /// <summary>
    /// Called after each turn with the messages it added (the plain question, any tool calls and
    /// results, the answer) — what a store needs to persist the conversation.
    /// </summary>
    public Func<IReadOnlyList<ChatMessage>, Task>? TurnCompleted { get; set; }

    /// <summary>The messages so far, system prompt included.</summary>
    public IReadOnlyList<ChatMessage> History => _history;

    public StudyAgent(
        LlmClient llm,
        LlmConfig llmConfig,
        ToolRegistry registry,
        StudyAgentOptions config,
        ILogger log)
    {
        _llm = llm;
        _llmConfig = llmConfig;
        _registry = registry;
        _config = config;
        _log = log;
        _registry.ToolStarted += (name, args) => ToolStarted?.Invoke(name, args);
    }

    // ── Public API ─────────────────────────────────────────────────────────

    public async Task<string> ChatAsync(
        string userInput,
        string? ragContext = null,
        bool includeSDA = false,
        QueryMode mode = QueryMode.Deep,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        await foreach (var text in ChatStreamAsync(userInput, ragContext, includeSDA, mode, ct))
            sb.Append(text);
        return sb.Length > 0 ? sb.ToString() : "(no response)";
    }

    /// <summary>Streams the answer as it is generated. Tool calls happen between chunks.</summary>
    public async IAsyncEnumerable<string> ChatStreamAsync(
        string userInput,
        string? ragContext = null,
        bool includeSDA = false,
        QueryMode mode = QueryMode.Deep,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureSystemPrompt();
        TrimHistory();

        if (ragContext is not null)
            _log.LogInformation("[Agent] RAG context ({Len} chars):\n{Ctx}",
                ragContext.Length, ragContext.Length > 2000 ? ragContext[..2000] + "…" : ragContext);
        else
            _log.LogInformation("[Agent] No RAG context for this query");

        var userMessage = new ChatMessage(ChatRole.User, ComposeUserMessage(userInput, ragContext, includeSDA, mode))
        {
            // The plain question, for building a transcript without the retrieved material.
            AdditionalProperties = new() { [TranscriptBuilder.QuestionProperty] = userInput },
        };
        var userIndex = _history.Count;
        _history.Add(userMessage);

        var options = BuildOptions();
        var updates = new List<ChatResponseUpdate>();
        var timer = Stopwatch.StartNew();
        var completed = false;

        try
        {
            await foreach (var update in _llm.Client.GetStreamingResponseAsync(_history, options, ct))
            {
                updates.Add(update);
                var text = TextOf(update);
                if (text.Length > 0) yield return text;
            }
            completed = true;
        }
        finally
        {
            // A cancelled or failed turn must not leave a dangling user message behind.
            if (!completed && _history.Count > userIndex) _history.RemoveRange(userIndex, _history.Count - userIndex);
        }

        var response = updates.ToChatResponse();
        _history.AddRange(response.Messages);

        // The material has done its job; keep only the plain question so history stays small.
        _history[userIndex] = new ChatMessage(ChatRole.User, userInput)
        {
            AdditionalProperties = userMessage.AdditionalProperties,
        };

        UsageTracker.Record(_log, _llm, response.Usage, timer.Elapsed);

        if (TurnCompleted is not null)
            await TurnCompleted(_history.GetRange(userIndex, _history.Count - userIndex));
    }

    /// <summary>
    /// Continues a stored conversation: replays its messages (without the system prompt, which is
    /// always rebuilt) so the next question sees them. Call on a fresh agent.
    /// </summary>
    public void LoadHistory(IEnumerable<ChatMessage> messages)
    {
        EnsureSystemPrompt();
        _history.AddRange(messages);
        TrimHistory();
    }

    public void Reset()
    {
        _history.Clear();
        _initialized = false;
        _conversationId = Guid.NewGuid().ToString("N"); // a new Claude Code session, too
        _log.LogInformation("[Agent] Conversation reset");
    }

    public int MessageCount => _history.Count;

    // ── Building the request ───────────────────────────────────────────────

    private void EnsureSystemPrompt()
    {
        if (_initialized) return;

        var text = new TextContent(_config.SystemPrompt ?? Prompts.System);
        if (_llm.SupportsPromptCaching)
            text.WithCacheControl(new Anthropic.Models.Messages.CacheControlEphemeral { Ttl = Anthropic.Models.Messages.Ttl.Ttl1h });

        _history.Add(new ChatMessage(ChatRole.System, [text]));
        _initialized = true;
    }

    private string ComposeUserMessage(string userInput, string? ragContext, bool includeSDA, QueryMode mode)
    {
        var parts = new List<string>();
        if (includeSDA && !string.IsNullOrWhiteSpace(_config.SdaInstructions))
            parts.Add(_config.SdaInstructions);
        if (mode == QueryMode.Compare && !string.IsNullOrWhiteSpace(_config.CompareInstructions))
            parts.Add(_config.CompareInstructions);
        if (ragContext is not null)
            parts.Add(ragContext);
        parts.Add(parts.Count > 0 ? $"User question: {userInput}" : userInput);
        return string.Join("\n\n", parts);
    }

    private ChatOptions BuildOptions()
    {
        var options = new ChatOptions { MaxOutputTokens = _llmConfig.MaxOutputTokens };

        if (_llm.SupportsTools)
        {
            options.Tools = [.. _registry.Tools];
            options.ToolMode = ChatToolMode.Auto;
        }

        if (_llm.SupportsTemperature)
            options.Temperature = _llmConfig.OllamaTemperature;

        if (_llm.OwnsConversationState)
            options.ConversationId = _conversationId;

        // How hard a hosted model thinks. (Claude Code takes --effort instead; Ollama has no such setting.)
        if (_llm.Provider is LlmProvider.Anthropic or LlmProvider.OpenAI &&
            Enum.TryParse<ReasoningEffort>(_llmConfig.Effort, ignoreCase: true, out var effort))
            options.Reasoning = new ReasoningOptions { Effort = effort };

        return options;
    }

    /// <summary>
    /// Keeps the conversation to about N exchanges. Once it passes N, it is cut back a little
    /// further (to three quarters of N) so the cut doesn't happen again on the very next turn —
    /// with Claude Code every cut means a new session. Whole exchanges are dropped together (a
    /// question, its tool calls and its answer), so no tool call is left without its result.
    /// </summary>
    private void TrimHistory()
    {
        var limit = Math.Max(1, _llmConfig.HistoryTurns);
        var userIndexes = _history.Select((m, i) => (m, i)).Where(x => x.m.Role == ChatRole.User).Select(x => x.i).ToList();
        if (userIndexes.Count <= limit) return;

        var keep = Math.Max(1, limit - limit / 4);
        var cutoff = userIndexes[^keep];
        var firstNonSystem = _history.FindIndex(m => m.Role != ChatRole.System);
        if (firstNonSystem < 0 || cutoff <= firstNonSystem) return;

        _history.RemoveRange(firstNonSystem, cutoff - firstNonSystem);
        _log.LogInformation("[Agent] History trimmed to the last {Keep} exchange(s)", keep);
    }

    private static string TextOf(ChatResponseUpdate update) =>
        string.Concat(update.Contents.OfType<TextContent>().Select(t => t.Text));
}
