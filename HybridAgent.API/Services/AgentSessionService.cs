using HybridAgent.Core;
using HybridAgent.Core.Agents;
using HybridAgent.Core.Models;
using HybridAgent.Core.Tools;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace HybridAgent.API.Services;

/// <summary>
/// Manages one HybridPipeline per SignalR connection.
/// Each connection gets its own agent instance with its own conversation history.
///
/// Lifetime: Singleton — the dictionary lives for the app lifetime.
/// Individual sessions are created on SelectAgent and removed on disconnect.
/// </summary>
public class AgentSessionService
{
    private readonly ILoggerFactory _logFactory;
    private readonly ILogger _log;
    private readonly string _openAiApiKey;

    // connectionId → active pipeline
    private readonly ConcurrentDictionary<string, HybridPipeline> _sessions = new();

    // connectionId → selected agent type (so we can report it back)
    private readonly ConcurrentDictionary<string, AgentType> _agentTypes = new();

    public AgentSessionService(ILoggerFactory logFactory, IConfiguration config)
    {
        _logFactory = logFactory;
        _log = logFactory.CreateLogger<AgentSessionService>();
        _openAiApiKey = config["OpenAI:ApiKey"]
                        ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                        ?? string.Empty;
    }

    // ── Session lifecycle ──────────────────────────────────────────────────

    /// <summary>
    /// Create or replace the pipeline for a connection with the chosen agent type.
    /// Also wires up the RAG index for that domain.
    /// </summary>
    public async Task<HybridPipeline> SelectAgentAsync(
        string connectionId,
        AgentType agentType,
        CancellationToken ct = default)
    {
        _log.LogInformation("[Session] {ConnId} selecting agent: {Agent}", connectionId, agentType);

        var (config, tools) = agentType switch
        {
            AgentType.Car => AgentFactory.CreateCarAgent(_openAiApiKey),
            AgentType.Bible => AgentFactory.CreateBibleAgent(_openAiApiKey),
            AgentType.CSharp => AgentFactory.CreateCSharpAgent(_openAiApiKey),
            _ => AgentFactory.CreateCarAgent(_openAiApiKey)
        };

        config.RagDocsDirectory = agentType switch
        {
            AgentType.Car => "docs/car",
            AgentType.Bible => "docs/bible",
            AgentType.CSharp => "docs/csharp",
            _ => "docs/car"
        };
        config.RagIndexPath = agentType switch
        {
            AgentType.Car => "index/car.json",
            AgentType.Bible => "index/bible.json",
            AgentType.CSharp => "index/csharp.json",
            _ => "index/car.json"
        };

        Directory.CreateDirectory(config.RagDocsDirectory);
        Directory.CreateDirectory("index");

        var pipeline = await HybridPipeline.CreateAsync(config, tools, _logFactory, ct);

        // Replace any existing session
        _sessions[connectionId] = pipeline;
        _agentTypes[connectionId] = agentType;

        return pipeline;
    }

    /// <summary>Get the active pipeline for a connection, or null if none selected.</summary>
    public HybridPipeline? GetPipeline(string connectionId) =>
        _sessions.TryGetValue(connectionId, out var p) ? p : null;

    public AgentType? GetAgentType(string connectionId) =>
        _agentTypes.TryGetValue(connectionId, out var t) ? t : null;

    /// <summary>Remove the session when the client disconnects.</summary>
    public void RemoveSession(string connectionId)
    {
        _sessions.TryRemove(connectionId, out _);
        _agentTypes.TryRemove(connectionId, out _);
        _log.LogInformation("[Session] {ConnId} removed", connectionId);
    }

    /// <summary>Re-index the RAG documents for the active agent on this connection.</summary>
    public async Task<RagIndexResult> ReindexAsync(
        string connectionId,
        CancellationToken ct = default)
    {
        var agentType = GetAgentType(connectionId);
        if (agentType is null)
            return new RagIndexResult(false, "No agent selected. Call SelectAgent first.");

        var docsDir = agentType switch
        {
            AgentType.Car => "docs/car",
            AgentType.Bible => "docs/bible",
            AgentType.CSharp => "docs/csharp",
            _ => "docs/car"
        };
        var indexPath = agentType switch
        {
            AgentType.Car => "index/car.json",
            AgentType.Bible => "index/bible.json",
            AgentType.CSharp => "index/csharp.json",
            _ => "index/car.json"
        };

        // Delete existing index to force rebuild
        if (File.Exists(indexPath))
            File.Delete(indexPath);

        // Re-select the agent — this triggers a fresh index build
        await SelectAgentAsync(connectionId, agentType.Value, ct);

        var pipeline = GetPipeline(connectionId)!;
        return new RagIndexResult(true,
            $"Indexed {pipeline.IndexedChunks} chunks from {docsDir}");
    }

    public RagStatusResult GetRagStatus(string connectionId)
    {
        var pipeline = GetPipeline(connectionId);
        var agentType = GetAgentType(connectionId);

        if (pipeline is null || agentType is null)
            return new RagStatusResult(false, 0, "No agent selected");

        var docsDir = agentType switch
        {
            AgentType.Car => "docs/car",
            AgentType.Bible => "docs/bible",
            AgentType.CSharp => "docs/csharp",
            _ => "docs/car"
        };

        var files = Directory.Exists(docsDir)
            ? Directory.GetFiles(docsDir, "*.txt", SearchOption.AllDirectories).Length
            : 0;

        return new RagStatusResult(
            pipeline.IndexedChunks > 0,
            pipeline.IndexedChunks,
            $"{files} .txt file(s) in {docsDir}");
    }
}

public enum AgentType { Car, Bible, CSharp }

public record RagIndexResult(bool Success, string Message);
public record RagStatusResult(bool HasIndex, int ChunkCount, string Details);