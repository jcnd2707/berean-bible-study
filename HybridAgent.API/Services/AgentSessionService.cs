using HybridAgent.Core.Agents;
using HybridAgent.Core.RAG;
using HybridAgent.Core;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace HybridAgent.API.Services;

/// <summary>
/// Manages one HybridPipeline per SignalR connection.
/// Language is stored per-connection and passed to the router at session creation.
/// </summary>
public class AgentSessionService
{
    private readonly ILoggerFactory _logFactory;
    private readonly ILogger _log;
    private readonly string _openAiApiKey;
    private readonly string _ollamaEndpoint;
    private readonly string _embeddingModel;
    private readonly AgentRagConfig? _bibleRagConfig;
    private readonly AgentRagConfig? _carRagConfig;
    private readonly AgentRagConfig? _codeRagConfig;

    private readonly ConcurrentDictionary<string, HybridPipeline> _sessions = new();
    private readonly ConcurrentDictionary<string, AgentType> _agentTypes = new();
    private readonly ConcurrentDictionary<string, string> _languages = new();

    public AgentSessionService(ILoggerFactory logFactory, IConfiguration config)
    {
        _logFactory = logFactory;
        _log = logFactory.CreateLogger<AgentSessionService>();
        _openAiApiKey = config["OpenAI:ApiKey"]
                          ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                          ?? string.Empty;
        _ollamaEndpoint = config["Ollama:Endpoint"] ?? "http://localhost:11434";
        _embeddingModel = config["Ollama:EmbeddingModel"] ?? "nomic-embed-text";
        _bibleRagConfig = BindRagConfig(config, "BibleAgent");
        _carRagConfig = BindRagConfig(config, "CarAgent");
        _codeRagConfig = BindRagConfig(config, "CodeAgent");
    }

    // ── Language ───────────────────────────────────────────────────────────

    /// <summary>Set the preferred language for a connection ("en" or "es").</summary>
    public void SetLanguage(string connectionId, string language)
    {
        _languages[connectionId] = language;
        _log.LogInformation("[Session] {ConnId} language set to {Lang}", connectionId, language);
    }

    public string GetLanguage(string connectionId) =>
        _languages.TryGetValue(connectionId, out var l) ? l : "en";

    // ── Session lifecycle ──────────────────────────────────────────────────

    public async Task<HybridPipeline> SelectAgentAsync(
        string connectionId,
        AgentType agentType,
        CancellationToken ct = default)
    {
        _log.LogInformation("[Session] {ConnId} selecting agent: {Agent}", connectionId, agentType);

        var language = GetLanguage(connectionId);

        HybridPipeline pipeline;

        if (agentType == AgentType.Bible && _bibleRagConfig is not null)
        {
            // Bible agent uses the dedicated factory with router + language
            var (agentConfig, _) = AgentFactory.CreateBibleAgent(_openAiApiKey);

            pipeline = await HybridPipeline.CreateBibleAsync(
                agentConfig, _logFactory, _bibleRagConfig,
                _ollamaEndpoint, _embeddingModel,
                language, ct);
        }
        else
        {
            var (agentConfig, tools) = agentType switch
            {
                AgentType.Car => AgentFactory.CreateCarAgent(_openAiApiKey),
                AgentType.CSharp => AgentFactory.CreateCSharpAgent(_openAiApiKey),
                _ => AgentFactory.CreateCarAgent(_openAiApiKey)
            };

            var ragConfig = agentType switch
            {
                AgentType.Car => _carRagConfig,
                AgentType.CSharp => _codeRagConfig,
                _ => null
            };

            pipeline = await HybridPipeline.CreateAsync(
                agentConfig, tools, _logFactory,
                ragConfig, _ollamaEndpoint, _embeddingModel, ct);
        }

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
        _languages.TryRemove(connectionId, out _);
        _log.LogInformation("[Session] {ConnId} removed", connectionId);
    }

    public async Task<RagIndexResult> ReindexAsync(
        string connectionId, CancellationToken ct = default)
    {
        var agentType = GetAgentType(connectionId);
        if (agentType is null) return new RagIndexResult(false, "No agent selected.");

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

        var lang = GetLanguage(connectionId);
        var details = ragConfig is null
            ? "RAG not configured"
            : $"Root: {ragConfig.ModulesRootPath} | Lang: {lang}";

        return new RagStatusResult(pipeline.IndexedChunks > 0, pipeline.IndexedChunks, details);
    }

    private static AgentRagConfig? BindRagConfig(IConfiguration config, string agentKey)
    {
        var section = config.GetSection($"Agents:{agentKey}");
        if (!section.Exists()) return null;
        var c = section.Get<AgentRagConfig>();
        return string.IsNullOrWhiteSpace(c?.ModulesRootPath) ? null : c;
    }
}

public enum AgentType { Car, Bible, CSharp }
public record RagIndexResult(bool Success, string Message);
public record RagStatusResult(bool HasIndex, int ChunkCount, string Details);