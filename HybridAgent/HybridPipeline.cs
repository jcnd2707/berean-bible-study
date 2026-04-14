using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OllamaSharp;
using OpenAI;
using HybridAgent.RAG;
using HybridAgent.Core.Agents;
using HybridAgent.Core.Models;
using HybridAgent.Core.RAG;
using HybridAgent.Core.Tools;

namespace HybridAgent;

/// <summary>
/// Hosts a conversational DiagnosticAgent (local Ollama) and an optional VerdictAgent (cloud).
///
/// Normal flow  → ChatAsync()        — always local, full conversation memory
/// Escalation   → GetVerdictAsync()  — only when API key is present
/// Verse lookup → GetVerseContext()  — verse-pinned RAG for Bible agent
/// </summary>
public class HybridPipeline
{
    private readonly DiagnosticAgent _diagnostic;
    private readonly VerdictAgent? _verdict;
    private readonly RagPipeline? _rag;
    private readonly AgentConfig _config;
    private readonly ILogger _log;

    private string _lastUserInput = string.Empty;

    public bool CloudAvailable => _verdict is not null;
    public int IndexedChunks => _rag?.IndexedChunks ?? 0;

    public HybridPipeline(
        AgentConfig config,
        ToolRegistry registry,
        ILoggerFactory logFactory,
        RagPipeline? rag = null)
    {
        _config = config;
        _rag = rag;
        _log = logFactory.CreateLogger<HybridPipeline>();

        // ── Local client ───────────────────────────────────────────────────
        IChatClient localClient = new OllamaApiClient(new Uri(config.OllamaEndpoint), config.OllamaModel);
        

        // ── Cloud client (optional) ────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(config.OpenAiApiKey))
        {
            IChatClient cloudClient = new OpenAIClient(config.OpenAiApiKey).GetChatClient(config.CloudModel) as IChatClient;

            _verdict = new VerdictAgent(
                cloudClient, config,
                logFactory.CreateLogger<VerdictAgent>());

            _log.LogInformation("[Pipeline] Cloud enabled: {Model}", config.CloudModel);
        }
        else
        {
            _verdict = null;
            _log.LogInformation("[Pipeline] No API key — local-only mode");
        }

        _diagnostic = new DiagnosticAgent(
            localClient, registry, config,
            logFactory.CreateLogger<DiagnosticAgent>());
    }

    // ── Chat ───────────────────────────────────────────────────────────────

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

    // ── Verse-pinned context ───────────────────────────────────────────────

    /// <summary>
    /// Returns context chunks that cover a specific verse.
    /// Call this from the WPF UI when the user selects a passage,
    /// then pass the result as additional context to ChatAsync.
    /// Returns null when no chunks cover that verse.
    /// </summary>
    public string? GetVerseContext(int bookNumber, int chapter, int verse) =>
        _rag?.BuildVerseContext(bookNumber, chapter, verse);

    // ── Verdict ────────────────────────────────────────────────────────────

    public async Task<VerdictResult?> GetVerdictAsync(CancellationToken ct = default)
    {
        if (_verdict is null)
        {
            _log.LogWarning("[Pipeline] Verdict requested but no API key configured");
            return null;
        }

        _log.LogInformation("[Pipeline] Escalating to cloud: {Model}", _config.CloudModel);
        var summary = await _diagnostic.BuildSummaryAsync(_lastUserInput, ct);
        return await _verdict.GetVerdictAsync(summary, ct);
    }

    public void Reset() => _diagnostic.Reset();
    public int MessageCount => _diagnostic.MessageCount;

    // ── Factory ────────────────────────────────────────────────────────────

    /// <summary>
    /// Single factory for all agents.
    /// Builds the RAG pipeline from AgentRagConfig (root path + allowed extensions).
    /// Pass null for ragConfig to disable RAG for this agent.
    /// </summary>
    public static async Task<HybridPipeline> CreateAsync(
        AgentConfig agentConfig,
        ToolRegistry registry,
        ILoggerFactory logFactory,
        AgentRagConfig? ragConfig = null,
        string ollamaEndpoint = "http://localhost:11434",
        string embeddingModel = "nomic-embed-text",
        CancellationToken ct = default)
    {
        RagPipeline? rag = null;

        if (ragConfig is not null)
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(ragConfig.RagDbPath) ?? "index");

            rag = await RagPipeline.CreateAsync(
                ragConfig, logFactory, embeddingModel, ollamaEndpoint, ct);
        }

        return new HybridPipeline(agentConfig, registry, logFactory, rag);
    }
}