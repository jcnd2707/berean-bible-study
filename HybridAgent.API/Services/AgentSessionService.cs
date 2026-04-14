using HybridAgent.Core.Agents;
using HybridAgent.Core.RAG;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace HybridAgent.API.Services;

/// <summary>
/// Manages one HybridPipeline per SignalR connection.
///
/// All RAG configuration comes from appsettings.json — nothing is hardcoded.
/// Config shape:
///   Agents:BibleAgent → AgentRagConfig (RagDbPath, ModulesRootPath, AllowedExtensions)
///   Agents:CarAgent   → AgentRagConfig
///   Agents:CodeAgent  → AgentRagConfig
///   Ollama:Endpoint, Ollama:EmbeddingModel
///   OpenAI:ApiKey
/// </summary>
public class AgentSessionService
{
    private readonly ILoggerFactory _logFactory;
    private readonly ILogger _log;
    private readonly string _openAiApiKey;
    private readonly string _ollamaEndpoint;
    private readonly string _embeddingModel;

    // Typed RAG config per agent — null means RAG is not configured for that agent
    private readonly AgentRagConfig? _bibleRagConfig;
    private readonly AgentRagConfig? _carRagConfig;
    private readonly AgentRagConfig? _codeRagConfig;

    private readonly ConcurrentDictionary<string, HybridPipeline> _sessions = new();
    private readonly ConcurrentDictionary<string, AgentType> _agentTypes = new();

    public AgentSessionService(ILoggerFactory logFactory, IConfiguration config)
    {
        _logFactory = logFactory;
        _log = logFactory.CreateLogger<AgentSessionService>();

        _openAiApiKey = config["OpenAI:ApiKey"]
                        ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                        ?? string.Empty;

        _ollamaEndpoint = config["Ollama:Endpoint"] ?? "http://localhost:11434";
        _embeddingModel = config["Ollama:EmbeddingModel"] ?? "nomic-embed-text";

        // Bind each agent's RAG config from the Agents section.
        // GetSection returns an empty section (not null) when the key is absent,
        // so we check that ModulesRootPath is actually set before using it.
        _bibleRagConfig = BindRagConfig(config, "BibleAgent");
        _carRagConfig = BindRagConfig(config, "CarAgent");
        _codeRagConfig = BindRagConfig(config, "CodeAgent");
    }

    // ── Session lifecycle ──────────────────────────────────────────────────

    public async Task<HybridPipeline> SelectAgentAsync(
        string connectionId,
        AgentType agentType,
        CancellationToken ct = default)
    {
        _log.LogInformation("[Session] {ConnId} selecting agent: {Agent}", connectionId, agentType);

        var (agentConfig, tools) = agentType switch
        {
            AgentType.Car => AgentFactory.CreateCarAgent(_openAiApiKey),
            AgentType.Bible => AgentFactory.CreateBibleAgent(_openAiApiKey),
            AgentType.CSharp => AgentFactory.CreateCSharpAgent(_openAiApiKey),
            _ => AgentFactory.CreateCarAgent(_openAiApiKey)
        };

        var ragConfig = agentType switch
        {
            AgentType.Bible => _bibleRagConfig,
            AgentType.Car => _carRagConfig,
            AgentType.CSharp => _codeRagConfig,
            _ => null
        };

        var pipeline = await HybridPipeline.CreateAsync(
            agentConfig, tools, _logFactory,
            ragConfig, _ollamaEndpoint, _embeddingModel, ct);

        _sessions[connectionId] = pipeline;
        _agentTypes[connectionId] = agentType;

        return pipeline;
    }

    public HybridPipeline? GetPipeline(string connectionId) =>
        _sessions.TryGetValue(connectionId, out var p) ? p : null;

    public AgentType? GetAgentType(string connectionId) =>
        _agentTypes.TryGetValue(connectionId, out var t) ? t : null;

    public void RemoveSession(string connectionId)
    {
        _sessions.TryRemove(connectionId, out _);
        _agentTypes.TryRemove(connectionId, out _);
        _log.LogInformation("[Session] {ConnId} removed", connectionId);
    }

    public async Task<RagIndexResult> ReindexAsync(
        string connectionId,
        CancellationToken ct = default)
    {
        var agentType = GetAgentType(connectionId);
        if (agentType is null)
            return new RagIndexResult(false, "No agent selected.");

        // Delete the rag.db for this agent to force a rebuild on next SelectAgent
        var ragConfig = agentType switch
        {
            AgentType.Bible => _bibleRagConfig,
            AgentType.Car => _carRagConfig,
            AgentType.CSharp => _codeRagConfig,
            _ => null
        };

        if (ragConfig is not null && File.Exists(ragConfig.RagDbPath))
            File.Delete(ragConfig.RagDbPath);

        await SelectAgentAsync(connectionId, agentType.Value, ct);

        var pipeline = GetPipeline(connectionId)!;
        return new RagIndexResult(true, $"Re-indexed {pipeline.IndexedChunks} chunks");
    }

    public RagStatusResult GetRagStatus(string connectionId)
    {
        var pipeline = GetPipeline(connectionId);
        var agentType = GetAgentType(connectionId);

        if (pipeline is null || agentType is null)
            return new RagStatusResult(false, 0, "No agent selected");

        var ragConfig = agentType switch
        {
            AgentType.Bible => _bibleRagConfig,
            AgentType.Car => _carRagConfig,
            AgentType.CSharp => _codeRagConfig,
            _ => null
        };

        var details = ragConfig is null
            ? "RAG not configured"
            : $"Root: {ragConfig.ModulesRootPath} | " +
              $"Extensions: {string.Join(", ", ragConfig.AllowedExtensions)}";

        return new RagStatusResult(pipeline.IndexedChunks > 0, pipeline.IndexedChunks, details);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static AgentRagConfig? BindRagConfig(IConfiguration config, string agentKey)
    {
        var section = config.GetSection($"Agents:{agentKey}");
        if (!section.Exists()) return null;

        var ragConfig = section.Get<AgentRagConfig>();

        // Treat as unconfigured if root path is missing or empty
        return string.IsNullOrWhiteSpace(ragConfig?.ModulesRootPath) ? null : ragConfig;
    }
}

public enum AgentType { Car, Bible, CSharp }

public record RagIndexResult(bool Success, string Message);
public record RagStatusResult(bool HasIndex, int ChunkCount, string Details);