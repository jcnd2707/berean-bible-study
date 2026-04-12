using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using HybridAgent.Core.Models;

namespace HybridAgent.Core.Agents;

/// <summary>
/// Phase 2 — sends the DiagnosisSummary to a cloud model for the final verdict.
/// Only called explicitly when the user types 'verdict'.
/// </summary>
public class VerdictAgent
{
    private readonly IChatClient _client;
    private readonly AgentConfig _config;
    private readonly ILogger _log;

    public VerdictAgent(IChatClient client, AgentConfig config, ILogger log)
    {
        _client = client;
        _config = config;
        _log = log;
    }

    public async Task<VerdictResult> GetVerdictAsync(
        DiagnosisSummary summary,
        CancellationToken ct = default)
    {
        _log.LogInformation("[Verdict] Sending to cloud model: {Model}", _config.CloudModel);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, BuildSystemPrompt()),
            new(ChatRole.User,   summary.ToCloudPrompt())
        };

        var response = await _client.GetResponseAsync(messages,
            new ChatOptions { Temperature = 0.3f }, ct);

        var text = response.Messages
            .Select(m => m.Text)
            .LastOrDefault(t => !string.IsNullOrWhiteSpace(t))
            ?? "(no response)";

        _log.LogInformation("[Verdict] Cloud model responded");

        return new VerdictResult
        {
            Summary = summary,
            VerdictText = text,
            ModelUsed = _config.CloudModel,
            CompletedAt = DateTime.UtcNow,
        };
    }

    private static string BuildSystemPrompt() => """
        You are a senior specialist delivering a final verdict based on a diagnostic
        report prepared by a local AI agent.

        Your verdict must be:
        - Direct — lead with the conclusion, not caveats
        - Grounded in the evidence — reference specific facts from the report
        - Honest about uncertainty — if confidence is low, say so
        - Actionable — always end with concrete next steps
        """;
}

public class VerdictResult
{
    public required DiagnosisSummary Summary { get; init; }
    public required string VerdictText { get; init; }
    public required string ModelUsed { get; init; }
    public required DateTime CompletedAt { get; init; }

    public void PrintToConsole()
    {
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════╗");
        Console.WriteLine("║                    FINAL VERDICT                    ║");
        Console.WriteLine($"║  via {ModelUsed,-48}║");
        Console.WriteLine("╚══════════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine(VerdictText);
        Console.WriteLine();
        Console.WriteLine($"  Completed: {CompletedAt:HH:mm:ss} UTC");
        Console.WriteLine("──────────────────────────────────────────────────────");
    }
}