namespace Berean.Core.Llm;

public enum LlmProvider
{
    /// <summary>Local models through Ollama (also the only provider that takes a sampling temperature).</summary>
    Ollama,

    /// <summary>Anthropic's API through the official SDK. Needs an API key.</summary>
    Anthropic,

    /// <summary>The Claude Code CLI in headless mode, using the login already on this machine (no API key).</summary>
    ClaudeCode,

    /// <summary>OpenAI's API. Needs an API key.</summary>
    OpenAI,
}

/// <summary>Bound from the "Llm" section of appsettings.json.</summary>
public class LlmConfig
{
    public LlmProvider Provider { get; set; } = LlmProvider.Ollama;

    /// <summary>Default model for the provider ("opus" for Claude Code, "claude-opus-5-5" for the API, …).</summary>
    public string Model { get; set; } = "";

    /// <summary>Models the UI may choose between for the hosted provider. Empty means just <see cref="Model"/>.</summary>
    public List<string> Models { get; set; } = [];

    /// <summary>
    /// Environment variable holding the API key. Empty uses the provider's usual one
    /// (ANTHROPIC_API_KEY, OPENAI_API_KEY). Keys are never read from appsettings.
    /// </summary>
    public string ApiKeyEnvVar { get; set; } = "";

    /// <summary>low | medium | high — how hard a hosted model thinks before answering.</summary>
    public string Effort { get; set; } = "medium";

    public int MaxOutputTokens { get; set; } = 8192;

    /// <summary>Most tool-call rounds in one answer.</summary>
    public int MaxToolRounds { get; set; } = 10;

    /// <summary>How many recent exchanges stay in the conversation before the oldest are dropped.</summary>
    public int HistoryTurns { get; set; } = 12;

    public string OllamaEndpoint { get; set; } = "http://localhost:11434";
    public float OllamaTemperature { get; set; } = 0.2f;

    public ClaudeCodeConfig ClaudeCode { get; set; } = new();

    public IReadOnlyList<string> HostedModels =>
        (Models.Count > 0 ? Models : string.IsNullOrWhiteSpace(Model) ? [] : [Model])
            .Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public string ApiKeyEnvVarFor(LlmProvider provider) =>
        !string.IsNullOrWhiteSpace(ApiKeyEnvVar) ? ApiKeyEnvVar
        : provider == LlmProvider.OpenAI ? "OPENAI_API_KEY"
        : "ANTHROPIC_API_KEY";
}

public class ClaudeCodeConfig
{
    /// <summary>
    /// "claude" (found on PATH), or a full path to claude.exe. On Windows an npm install puts a
    /// claude.cmd shim on PATH; the real claude.exe next to it is used instead.
    /// </summary>
    public string ExecutablePath { get; set; } = "claude";

    /// <summary>
    /// Where the CLI runs and keeps its sessions. Should be an empty folder so it never picks
    /// up a repository's files or a project CLAUDE.md. Blank uses a folder under LocalAppData.
    /// </summary>
    public string WorkingDirectory { get; set; } = "";

    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>Passed as --effort when set (low, medium, high, xhigh, max).</summary>
    public string Effort { get; set; } = "";
}
