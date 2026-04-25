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
    private readonly string _defaultModel;
    private readonly AgentRagConfig? _bibleRagConfig;
    private readonly AgentRagConfig? _carRagConfig;
    private readonly AgentRagConfig? _codeRagConfig;

    private readonly ConcurrentDictionary<string, HybridPipeline> _sessions = new();
    private readonly ConcurrentDictionary<string, AgentType> _agentTypes = new();
    private readonly ConcurrentDictionary<string, string> _languages = new();
    private readonly ConcurrentDictionary<string, string> _models = new();
    // Per-session CTS so RemoveSession() can cancel background indexing on disconnect
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _sessionCts = new();

    public AgentSessionService(ILoggerFactory logFactory, IConfiguration config)
    {
        _logFactory = logFactory;
        _log = logFactory.CreateLogger<AgentSessionService>();
        _openAiApiKey = config["OpenAI:ApiKey"]
                          ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                          ?? string.Empty;
        _ollamaEndpoint = config["Ollama:Endpoint"] ?? "http://localhost:11434";
        _embeddingModel = config["Ollama:EmbeddingModel"] ?? "mxbai-embed-large";
        _defaultModel = config["Ollama:DefaultModel"] ?? "llama3.1:8b";
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
        string? modelId = null,
        CancellationToken connectionCt = default)
    {
        _log.LogInformation("[Session] {ConnId} selecting agent: {Agent}", connectionId, agentType);

        // Cancel any previous session's background work for this connection
        if (_sessionCts.TryRemove(connectionId, out var oldCts))
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }

        // New CTS linked to the connection abort token
        var cts = CancellationTokenSource.CreateLinkedTokenSource(connectionCt);
        _sessionCts[connectionId] = cts;
        var ct = cts.Token;

        var resolvedModel = string.IsNullOrWhiteSpace(modelId) ? _defaultModel : modelId;
        _models[connectionId] = resolvedModel;
        _log.LogInformation("[Session] {ConnId} model: {Model}", connectionId, resolvedModel);

        var language = GetLanguage(connectionId);

        HybridPipeline pipeline;

        if (agentType == AgentType.Bible && _bibleRagConfig is not null)
        {
            // Bible agent uses the dedicated factory with router + language
            var (agentConfig, _) = AgentFactory.CreateBibleAgent(_openAiApiKey, modelId: resolvedModel);

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
        if (_sessionCts.TryRemove(connectionId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
        _sessions.TryRemove(connectionId, out _);
        _agentTypes.TryRemove(connectionId, out _);
        _languages.TryRemove(connectionId, out _);
        _models.TryRemove(connectionId, out _);
        _log.LogInformation("[Session] {ConnId} removed", connectionId);
    }

    public async Task<RagIndexResult> ReindexAsync(
        string connectionId, CancellationToken connectionCt = default)
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

        var currentModel = _models.GetValueOrDefault(connectionId);
        await SelectAgentAsync(connectionId, agentType.Value, currentModel, connectionCt);
        return new RagIndexResult(true, "Re-index started in background.");
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