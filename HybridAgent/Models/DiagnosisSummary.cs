using HybridAgent.Agents;
using static System.Net.Mime.MediaTypeNames;

namespace HybridAgent.Models;

public class DiagnosisSummary
{
    public required string OriginalInput { get; init; }
    public List<ToolResult> CollectedFacts { get; init; } = [];
    public required string LocalAnalysis { get; init; }
    public int LocalConfidence { get; init; }

    public string ToCloudPrompt() => $"""
        ## Diagnostic Report

        **Original user input:**
        {OriginalInput}

        **Evidence gathered (via tools):**
        {string.Join("\n", CollectedFacts.Select((f, i) => $"{i + 1}. [{f.ToolName}] {f.Result}"))}

        **Local model analysis:**
        {LocalAnalysis}

        **Local confidence:** {LocalConfidence}/100

        ---
        Based on the above diagnostic report, please provide:
        1. A final verdict (clear, direct conclusion)
        2. Confidence level (0-100) and reasoning
        3. Recommended next steps (if any)
        4. Any important caveats or limitations
        """;

    internal VerdictResult AsVeredict()
    {
        return new VerdictResult
        {
            Summary = this,
            VerdictText = this.LocalAnalysis,
            ModelUsed = "local",
            CompletedAt = DateTime.UtcNow
        };
    }
}
