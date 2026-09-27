using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OllamaSharp;
using OpenAI;

namespace Berean.Core.Llm;

/// <summary>A chat client plus what the agent needs to know about the provider behind it.</summary>
public sealed record LlmClient(IChatClient Client, LlmProvider Provider, string Model)
{
    /// <summary>Claude Code runs without tools; the router pre-fetches everything instead.</summary>
    public bool SupportsTools => Provider != LlmProvider.ClaudeCode;

    /// <summary>Current Claude models reject sampling parameters; only Ollama takes a temperature.</summary>
    public bool SupportsTemperature => Provider == LlmProvider.Ollama;

    /// <summary>Anthropic's API supports prompt-cache breakpoints on the system prompt.</summary>
    public bool SupportsPromptCaching => Provider == LlmProvider.Anthropic;

    /// <summary>Claude Code keeps the conversation in its own session and resumes it by id.</summary>
    public bool OwnsConversationState => Provider == LlmProvider.ClaudeCode;
}

/// <summary>
/// Builds the <see cref="IChatClient"/> for a provider, wrapped with function invocation (the
/// tool loop, which works with any provider and streams) and logging. Only Ollama does
/// embeddings — those stay local whatever provider answers.
/// </summary>
public static class ChatClientFactory
{
    public static LlmClient Create(
        LlmConfig config, LlmProvider provider, string model, ILoggerFactory logFactory)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException($"No model configured for provider {provider}. Set Llm:Model.");

        IChatClient inner = provider switch
        {
            LlmProvider.Ollama => new OllamaApiClient(new Uri(config.OllamaEndpoint), model),
            LlmProvider.Anthropic => CreateAnthropic(config, model),
            LlmProvider.OpenAI => CreateOpenAI(config, model),
            LlmProvider.ClaudeCode => new ClaudeCodeChatClient(
                config.ClaudeCode, model, logFactory.CreateLogger<ClaudeCodeChatClient>()),
            _ => throw new NotSupportedException($"Unknown provider {provider}"),
        };

        var client = inner
            .AsBuilder()
            .UseFunctionInvocation(logFactory, f => f.MaximumIterationsPerRequest = config.MaxToolRounds)
            .UseLogging(logFactory)
            .Build();

        return new LlmClient(client, provider, model);
    }

    private static IChatClient CreateAnthropic(LlmConfig config, string model)
    {
        var key = RequireKey(config, LlmProvider.Anthropic);
        return new AnthropicClient { ApiKey = key }.AsIChatClient(model, config.MaxOutputTokens);
    }

    private static IChatClient CreateOpenAI(LlmConfig config, string model)
    {
        var key = RequireKey(config, LlmProvider.OpenAI);
        return new OpenAIClient(key).GetChatClient(model).AsIChatClient();
    }

    private static string RequireKey(LlmConfig config, LlmProvider provider)
    {
        var envVar = config.ApiKeyEnvVarFor(provider);
        var key = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                $"The {provider} provider needs an API key. Set the {envVar} environment variable " +
                "(keys are never read from appsettings.json).");
        return key;
    }
}
