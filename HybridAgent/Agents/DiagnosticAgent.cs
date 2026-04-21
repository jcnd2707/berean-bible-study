using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using HybridAgent.Core.Models;
using HybridAgent.Core.Tools;

namespace HybridAgent.Core.Agents;

/// <summary>
/// Conversational agent backed by local Ollama.
/// _history is a field — persists across every ChatAsync() call so follow-up
/// questions retain full context ("when did he die?" knows who "he" is).
/// </summary>
public class DiagnosticAgent
{
    private readonly IChatClient _client;
    private readonly ToolRegistry _registry;
    private readonly AgentConfig _config;
    private readonly ILogger _log;
    private readonly List<ChatMessage> _history = [];
    private bool _initialized = false;

    public DiagnosticAgent(
        IChatClient client,
        ToolRegistry registry,
        AgentConfig config,
        ILogger log)
    {
        _client = client;
        _registry = registry;
        _config = config;
        _log = log;
    }

    // ── Public API ─────────────────────────────────────────────────────────

    public async Task<string> ChatAsync(
        string userInput,
        string? ragContext = null,
        CancellationToken ct = default)
    {
        if (!_initialized)
        {
            _history.Add(new ChatMessage(ChatRole.System, BuildSystemPrompt(_config.SystemPrompt)));
            _initialized = true;
        }

        if (ragContext is not null)
            _log.LogInformation("[Agent] RAG context ({Len} chars):\n{Ctx}",
                ragContext.Length, ragContext.Length > 2000 ? ragContext[..2000] + "…" : ragContext);
        else
            _log.LogInformation("[Agent] No RAG context for this query");

        // Question first so the model knows what's being asked;
        // RAG context follows immediately so it's the freshest content in the user turn.
        var userMessage = ragContext is not null
            ? $"User question: {userInput}\n\n{ragContext}"
            : userInput;

        _history.Add(new ChatMessage(ChatRole.User, userMessage));

        var options = new ChatOptions
        {
            Temperature = 0.2f,
            Tools = [.. _registry.Tools],
            ToolMode = ChatToolMode.Auto,
        };

        return await RunLoopAsync(options, ragContext, ct);
    }

    public async Task<DiagnosisSummary> BuildSummaryAsync(
        string originalInput,
        CancellationToken ct = default)
    {
        var summaryHistory = new List<ChatMessage>(_history)
        {
            new(ChatRole.User, """
                Summarise the conversation so far as a JSON object (no markdown fences):
                {
                  "analysis": "<concise summary of findings and reasoning>",
                  "confidence": <integer 0-100>
                }
                """)
        };

        var response = await _client.GetResponseAsync(summaryHistory,
                            new ChatOptions { Temperature = 0.1f }, ct);
        var text = response.Messages.LastOrDefault()?.Text ?? "{}";
        string analysis = "Unable to parse summary.";
        int confidence = 0;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text.Trim());
            analysis = doc.RootElement.GetProperty("analysis").GetString() ?? analysis;
            confidence = doc.RootElement.GetProperty("confidence").GetInt32();
        }
        catch
        {
            analysis = text;
            _log.LogWarning("[Agent] Non-JSON summary — using raw text");
        }

        return new DiagnosisSummary
        {
            OriginalInput = originalInput,
            CollectedFacts = [.. _registry.Invocations],
            LocalAnalysis = analysis,
            LocalConfidence = confidence
        };
    }

    public void Reset()
    {
        _history.Clear();
        _initialized = false;
        _log.LogInformation("[Agent] Conversation reset");
    }

    public int MessageCount => _history.Count;

    // ── Tool loop ──────────────────────────────────────────────────────────

    private async Task<string> RunLoopAsync(ChatOptions options, string? ragContext, CancellationToken ct)
    {
        int rounds = 0;
        bool ragReminderAdded = false;

        while (rounds++ < _config.MaxToolRounds)
        {
            _log.LogInformation("[Agent] Round {Round}, {Len} messages in history", rounds, _history.Count);

            var response = await _client.GetResponseAsync(_history, options, ct);

            // Collect tool calls from all messages in the response batch
            var allToolCalls = new List<FunctionCallContent>();
            foreach (var msg in response.Messages)
            {
                _history.Add(msg);
                allToolCalls.AddRange(msg.Contents.OfType<FunctionCallContent>());
            }

            // No tool calls → model produced its final answer
            if (allToolCalls.Count == 0)
            {
                var answer = response.Messages
                    .Select(m => m.Text)
                    .LastOrDefault(t => !string.IsNullOrWhiteSpace(t));

                _log.LogInformation("[Agent] Answered in {Rounds} round(s)", rounds);
                return answer ?? "(no response)";
            }

            // Execute every tool call and append results before the next round
            foreach (var call in allToolCalls)
            {
                _log.LogInformation("[Tool] {Name}", call.Name);

                // Look up by .Name — no .Metadata needed in v10.4+
                var tool = _registry.Tools.FirstOrDefault(t => t.Name == call.Name);
                string resultText;

                if (tool is not AIFunction fn)
                {
                    resultText = $"Error: unknown or non-invokable tool '{call.Name}'";
                    _log.LogWarning("[Tool] Not found: {Name}", call.Name);
                }
                else
                {
                    try
                    {
                        // Arguments is IDictionary<string,object?> — wrap in AIFunctionArguments
                        var args = new AIFunctionArguments(
                            call.Arguments ?? new Dictionary<string, object?>());
                        var raw = await fn.InvokeAsync(args, ct);
                        resultText = raw?.ToString() ?? "(null)";
                        _log.LogInformation("[Tool] {Name} → {Result}", call.Name, resultText);
                    }
                    catch (Exception ex)
                    {
                        resultText = $"Error: {ex.Message}";
                        _log.LogError(ex, "[Tool] {Name} threw", call.Name);
                    }
                }

                _history.Add(new ChatMessage(ChatRole.Tool,
                [
                    new FunctionResultContent(call.CallId, resultText)
                ]));
            }

            // After the first batch of tool results, remind the model to use
            // the reference material from the user message — counters recency bias.
            // Use ChatRole.Assistant so Ollama accepts it mid-conversation.
            if (ragContext is not null && !ragReminderAdded)
            {
                _history.Add(new ChatMessage(ChatRole.Assistant,
                    "I have the tool results. Now I will answer using the REFERENCE MATERIAL " +
                    "and the exact verse text from the [Verse text:] tag."));
                ragReminderAdded = true;
            }
        }

        _log.LogWarning("[Agent] Hit MaxToolRounds ({Max})", _config.MaxToolRounds);
        return "Reached the maximum number of tool calls. Please rephrase your question.";
    }

    private static string BuildSystemPrompt(string? domainPrompt) =>
        domainPrompt ?? """
            You are a precise and helpful assistant. Use your tools to gather facts
            before answering. Be concise and factual. Always remember the full
            context of the conversation when answering follow-up questions.
            """;
}