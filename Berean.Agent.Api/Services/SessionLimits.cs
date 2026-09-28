namespace Berean.Agent.Api.Services;

/// <summary>
/// Bound from the "Sessions" section of appsettings.json — the length limit a study session hits
/// before it becomes read-only and offers "Continue in a new session" (PROFILES_AND_SESSIONS_PLAN.md
/// Phase 5). Start values are deliberately config, not constants: after a week of use, the
/// "[Usage] in=" log lines show how quickly Deep/Compare questions actually grow the context, and
/// these can be tuned from that.
/// </summary>
public class SessionLimits
{
    public const string SectionName = "Sessions";

    public int MaxQuestions { get; set; } = 12;
    public int WarnQuestionsLeft { get; set; } = 3;
    public int MaxContextTokens { get; set; } = 60000;
    public double WarnContextFraction { get; set; } = 0.8;
}

/// <summary>Where a session stands against its limits — sent to the client after every turn.</summary>
public record LimitState(
    int QuestionsUsed,
    int MaxQuestions,
    int ContextTokens,
    int MaxContextTokens,
    string State); // "ok" | "nearing" | "full"

public static class LimitStateExtensions
{
    /// <summary>Full = either limit reached; nearing = within the configured warning margin of either.</summary>
    public static LimitState Evaluate(this SessionLimits limits, int questionsUsed, int contextTokens)
    {
        var full = questionsUsed >= limits.MaxQuestions || contextTokens >= limits.MaxContextTokens;
        var nearingQuestions = questionsUsed >= limits.MaxQuestions - limits.WarnQuestionsLeft;
        var nearingContext = contextTokens >= limits.MaxContextTokens * limits.WarnContextFraction;

        var state = full ? "full" : (nearingQuestions || nearingContext) ? "nearing" : "ok";
        return new LimitState(questionsUsed, limits.MaxQuestions, contextTokens, limits.MaxContextTokens, state);
    }
}
