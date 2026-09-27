namespace Berean.Core.Agent;

/// <summary>
/// The agent's prompts. Which model answers, and how, lives in <see cref="Llm.LlmConfig"/>.
/// </summary>
public class StudyAgentOptions
{
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Added to the user turn (not the system prompt) once per selected perspective, with
    /// "{Label}", "{LabelUpper}" and "{Prefix}" filled in. See Prompts/perspective-addendum.md.
    /// </summary>
    public string? PerspectiveAddendumTemplate { get; set; }

    /// <summary>Added to the user turn in Compare mode.</summary>
    public string? CompareInstructions { get; set; }
}
