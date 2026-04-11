namespace HybridAgent.Models;

/// <summary>
/// Configuration for one agent instance. Each domain agent gets its own.
/// </summary>
public class AgentConfig
{
    // Local
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "llama3.2:3b";
    public int MaxToolRounds { get; set; } = 10;

    // Cloud
    public string? OpenAiApiKey { get; set; } = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    public string CloudModel { get; set; } = "gpt-4o";

    // Domain identity
    public string? SystemPrompt { get; set; }

    // RAG — set both to enable document retrieval for this agent
    public string? RagDocsDirectory { get; set; } = "D:\\Bible Study\\bible-docs"; // folder with your .txt documents
    public string? RagIndexPath { get; set; } = "D:\\Bible Study"; // where to persist the vector index

    // Behaviour
    public bool VerboseLogging { get; set; } = true;
}