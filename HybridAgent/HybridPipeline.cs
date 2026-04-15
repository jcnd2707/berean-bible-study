using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OllamaSharp;
using OpenAI;
using HybridAgent.Core.Agents;
using HybridAgent.Core.Models;
using HybridAgent.Core.RAG;
using HybridAgent.Core.Tools;

namespace HybridAgent.Core;

/// <summary>
/// Hosts a conversational DiagnosticAgent (local Ollama) and an optional VerdictAgent (cloud).
///
/// Flow for every ChatAsync() call:
///   1. QueryRouter classifies the query (Verse / Definition / Conceptual / Mixed)
///   2. Router performs targeted retrieval — verse-pinned, tool hint, or multi-source MMR
///   3. Pre-fetched context is injected into DiagnosticAgent.ChatAsync()
///   4. Model synthesizes the answer; tool loop handles lookup_word calls
/// </summary>
public class HybridPipeline
{
    private readonly DiagnosticAgent _diagnostic;
    private readonly VerdictAgent? _verdict;
    private readonly QueryRouter? _router;
    private readonly AgentConfig _config;
    private readonly AgentRagConfig? _ragConfig;
    private readonly ILogger _log;

    private string _lastUserInput = string.Empty;

    public bool CloudAvailable => _verdict is not null;
    public int IndexedChunks => _router is null ? 0 : -1; // -1 = router owns the store

    public HybridPipeline(
        AgentConfig config,
        ToolRegistry registry,
        ILoggerFactory logFactory,
        QueryRouter? router = null,
        AgentRagConfig? ragConfig = null)
    {
        _config = config;
        _router = router;
        _ragConfig = ragConfig;
        _log = logFactory.CreateLogger<HybridPipeline>();

        // ── Local client ───────────────────────────────────────────────────
        IChatClient localClient = new OllamaApiClient(new Uri(config.OllamaEndpoint), config.OllamaModel);

        // ── Cloud client (optional) ────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(config.OpenAiApiKey))
        {
            IChatClient cloudClient = new OpenAIClient(config.OpenAiApiKey).GetChatClient(config.CloudModel) as IChatClient;

            _verdict = new VerdictAgent(cloudClient, config,
                logFactory.CreateLogger<VerdictAgent>());

            _log.LogInformation("[Pipeline] Cloud enabled: {Model}", config.CloudModel);
        }
        else
        {
            _verdict = null;
            _log.LogInformation("[Pipeline] No API key — local-only mode");
        }

        _diagnostic = new DiagnosticAgent(localClient, registry, config,
            logFactory.CreateLogger<DiagnosticAgent>());
    }

    // ── Chat ───────────────────────────────────────────────────────────────

    public async Task<string> ChatAsync(
        string userInput,
        CancellationToken ct = default)
    {
        _lastUserInput = userInput;

        string? ragContext = null;

        if (_router is not null)
        {
            // Step 4: pre-router classifies and retrieves targeted context
            var result = await _router.RouteAsync(userInput, ct);
            ragContext = result.Context;

            _log.LogInformation("[Pipeline] Intent={Intent} context={HasCtx}",
                result.Intent, ragContext is not null);
        }

        return await _diagnostic.ChatAsync(userInput, ragContext, ct);
    }

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
    /// Generic factory for Car and CSharp agents (no router, optional plain-text RAG).
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
        QueryRouter? router = null;

        if (ragConfig is not null)
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(ragConfig.RagDbPath) ?? "index");

            var rag = await RagPipeline.CreateAsync(
                ragConfig, logFactory, embeddingModel, ollamaEndpoint, ct);

            // Non-Bible agents use a simple conceptual-only router
            router = new QueryRouter(rag, ragConfig, ragConfig.Language,
                logFactory.CreateLogger<QueryRouter>());
        }

        return new HybridPipeline(agentConfig, registry, logFactory, router, ragConfig);
    }

    /// <summary>
    /// Bible agent factory — builds the full router with language awareness
    /// and wires dictionary files into the lookup_word tool.
    /// </summary>
    public static async Task<HybridPipeline> CreateBibleAsync(
        AgentConfig agentConfig,
        ILoggerFactory logFactory,
        AgentRagConfig ragConfig,
        string ollamaEndpoint,
        string embeddingModel,
        string language = "en",
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(ragConfig.RagDbPath) ?? "index");

        var rag = await RagPipeline.CreateAsync(
            ragConfig, logFactory, embeddingModel, ollamaEndpoint, ct);

        // Resolve dictionary files for the lookup_word tool
        var dictFiles = ragConfig.ResolveDictionaryFiles().ToList();

        // Build tools with dictionary files injected
        var (_, tools) = AgentFactory.CreateBibleAgent(
            agentConfig.OpenAiApiKey, dictFiles);

        // Build router with language from config (can be overridden at runtime)
        var routerLanguage = string.IsNullOrWhiteSpace(language)
            ? ragConfig.Language : language;

        var router = new QueryRouter(rag, ragConfig, routerLanguage,
            logFactory.CreateLogger<QueryRouter>());

        return new HybridPipeline(agentConfig, tools, logFactory, router, ragConfig);
    }
}