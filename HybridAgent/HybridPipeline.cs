using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OllamaSharp;
using OpenAI;
using HybridAgent.Core.Agents;
using HybridAgent.Core.Models;
using HybridAgent.Core.Tools;
using HybridAgent.Core.RAG;

namespace HybridAgent.Core;

/// <summary>
/// Hosts a conversational DiagnosticAgent (local Ollama) and an optional VerdictAgent (cloud).
///
/// Normal flow  → ChatAsync()       — always local, full conversation memory
/// Escalation   → GetVerdictAsync() — only available when an API key was provided
/// </summary>
public class HybridPipeline
{
    private readonly DiagnosticAgent _diagnostic;
    private readonly VerdictAgent? _verdict;      // null when no API key provided
    private readonly RagPipeline? _rag;
    private readonly AgentConfig _config;
    private readonly ILogger _log;

    private string _lastUserInput = string.Empty;

    public bool CloudAvailable => _verdict is not null;

    public HybridPipeline(
        AgentConfig config,
        ToolRegistry registry,
        ILoggerFactory logFactory,
        RagPipeline? rag = null)
    {
        _config = config;
        _rag = rag;
        _log = logFactory.CreateLogger<HybridPipeline>();

        // ── Local client (always required) ─────────────────────────────────
        IChatClient localClient = new OllamaApiClient(new Uri(config.OllamaEndpoint), config.OllamaModel);

        _diagnostic = new DiagnosticAgent(
            localClient, registry, config,
            logFactory.CreateLogger<DiagnosticAgent>());

        // ── Cloud client (optional) ────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(config.OpenAiApiKey))
        {
            IChatClient cloudClient = new OpenAIClient(config.OpenAiApiKey).GetChatClient(config.CloudModel) as IChatClient;

            _verdict = new VerdictAgent(
                cloudClient, config,
                logFactory.CreateLogger<VerdictAgent>());

            _log.LogInformation("[Pipeline] Cloud model enabled: {Model}", config.CloudModel);
        }
        else
        {
            _verdict = null;
            _log.LogInformation("[Pipeline] No API key — running local-only mode");
        }
    }

    // ── Chat — always local ────────────────────────────────────────────────

    public async Task<string> ChatAsync(
        string userInput,
        CancellationToken ct = default)
    {
        _lastUserInput = userInput;

        string? ragContext = null;
        if (_rag is not null && _rag.IndexedChunks > 0)
        {
            ragContext = await _rag.BuildContextAsync(userInput, topK: 5, ct: ct);
            if (ragContext is not null)
                _log.LogDebug("[Pipeline] RAG injected {Chars} chars", ragContext.Length);
        }

        return await _diagnostic.ChatAsync(userInput, ragContext, ct);
    }

    // ── Verdict — cloud only, explicitly requested ─────────────────────────

    /// <summary>
    /// Escalates the current conversation to the cloud model for a final verdict.
    /// Returns null and logs a warning when no API key was configured.
    /// </summary>
    public async Task<VerdictResult?> GetVerdictAsync(CancellationToken ct = default)
    {
        if (_verdict is null)
        {
            _log.LogWarning("[Pipeline] Cloud verdict requested but no API key configured");
            return null;
        }

        _log.LogInformation("[Pipeline] Escalating to cloud model: {Model}", _config.CloudModel);
        var summary = await _diagnostic.BuildSummaryAsync(_lastUserInput, ct);
        return await _verdict.GetVerdictAsync(summary, ct);
    }

    public void Reset() => _diagnostic.Reset();
    public int MessageCount => _diagnostic.MessageCount;

    // ── Factory ────────────────────────────────────────────────────────────

    public static async Task<HybridPipeline> CreateAsync(
        AgentConfig config,
        ToolRegistry registry,
        ILoggerFactory logFactory,
        CancellationToken ct = default)
    {
        RagPipeline? rag = null;

        if (!string.IsNullOrWhiteSpace(config.RagDocsDirectory) &&
            !string.IsNullOrWhiteSpace(config.RagIndexPath))
        {
            rag = await RagPipeline.CreateAsync(
                config.RagDocsDirectory,
                config.RagIndexPath,
                logFactory,
                ct: ct);
        }

        return new HybridPipeline(config, registry, logFactory, rag);
    }
}