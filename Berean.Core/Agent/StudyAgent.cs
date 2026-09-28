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
///   [Perspective / Compare instructions] + [retrieved material] + "User question: …"
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
    /// results, the answer) and that turn's context size — what a store needs to persist the
    /// conversation and enforce the session length limit (Phase 5).
    /// </summary>
    public Func<AgentTurn, Task>? TurnCompleted { get; set; }

    /// <summary>
    /// A recap of an earlier, now-full session (Phase 5's "continue in a new session"), put before
    /// the retrieved material on the first turn only. Because it rides in that first user message,
    /// it's stored and replayed on resume like anything else — no special-casing needed there.
    /// </summary>
    public string? CarryOver { get; set; }

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
        IReadOnlyList<Perspective>? perspectives = null,
        QueryMode mode = QueryMode.Deep,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        await foreach (var text in ChatStreamAsync(userInput, ragContext, perspectives, mode, ct))
            sb.Append(text);
        return sb.Length > 0 ? sb.ToString() : "(no response)";
    }

    /// <summary>Streams the answer as it is generated. Tool calls happen between chunks.</summary>
    public async IAsyncEnumerable<string> ChatStreamAsync(
        string userInput,
        string? ragContext = null,
        IReadOnlyList<Perspective>? perspectives = null,
        QueryMode mode = QueryMode.Deep,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        perspectives ??= [];
        EnsureSystemPrompt();
        TrimHistory();

        if (ragContext is not null)
            _log.LogInformation("[Agent] RAG context ({Len} chars):\n{Ctx}",
                ragContext.Length, ragContext.Length > 2000 ? ragContext[..2000] + "…" : ragContext);
        else
            _log.LogInformation("[Agent] No RAG context for this query");

        var isFirstTurn = _history.Count == 1; // just the system prompt so far — never true after LoadHistory
        var userMessage = new ChatMessage(ChatRole.User, ComposeUserMessage(userInput, ragContext, perspectives, mode, isFirstTurn))
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
        {
            var contextTokens = ComputeContextTokens(updates);
            await TurnCompleted(new AgentTurn(_history.GetRange(userIndex, _history.Count - userIndex), contextTokens));
        }
    }

    /// <summary>
    /// The context size the model actually held for this turn — the largest single request's
    /// input tokens, not the sum (function-invoking providers sum every tool round in
    /// updates.ToChatResponse().Usage, which overstates it) and not just the last request (which
    /// can understate a turn that briefly ballooned). This feeds a hard limit (Phase 5), so it
    /// measures the worst case, not the latest one.
    /// </summary>
    private int ComputeContextTokens(List<ChatResponseUpdate> updates)
    {
        var reported = updates
            .SelectMany(u => u.Contents.OfType<UsageContent>())
            .Select(u => u.Details.InputTokenCount)
            .Where(v => v is > 0)
            .Select(v => v!.Value)
            .DefaultIfEmpty(0L)
            .Max();

        if (reported > 0) return (int)Math.Min(reported, int.MaxValue);

        // No usage reported at all (Ollama can be silent on this) — estimate from what was sent.
        var estimatedChars = _history.Sum(m => (long)(m.Text?.Length ?? 0));
        return (int)Math.Min(estimatedChars / 4, int.MaxValue);
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

    private string ComposeUserMessage(
        string userInput, string? ragContext, IReadOnlyList<Perspective> perspectives, QueryMode mode, bool isFirstTurn)
    {
        var parts = new List<string>();
        if (isFirstTurn && !string.IsNullOrWhiteSpace(CarryOver))
            parts.Add($"[Recap of the earlier part of this study]\n{CarryOver}");
        if (!string.IsNullOrWhiteSpace(_config.PerspectiveAddendumTemplate))
            foreach (var p in perspectives)
                parts.Add(_config.PerspectiveAddendumTemplate
                    .Replace("{Label}", p.Label)
                    .Replace("{LabelUpper}", p.Label.ToUpperInvariant())
                    .Replace("{Prefix}", p.CitationPrefix));
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

/// <summary>What one turn added to the agent's history, and the context size it took to produce it.</summary>
public sealed record AgentTurn(IReadOnlyList<ChatMessage> Messages, int ContextTokens);
