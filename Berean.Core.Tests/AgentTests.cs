using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Berean.Core.Tests;

/// <summary>Records what it is sent and streams back canned text.</summary>
internal sealed class RecordingChatClient : IChatClient
{
    public List<List<ChatMessage>> Calls { get; } = [];
    public List<ChatOptions?> Options { get; } = [];
    public string[] Reply { get; set; } = ["Hello", " there"];
    public Exception? FailAfterFirstChunk { get; set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        => throw new NotSupportedException();

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Copy: the agent keeps mutating its own list.
        Calls.Add(messages.Select(m => new ChatMessage(m.Role, m.Text)).ToList());
        Options.Add(options);

        foreach (var chunk in Reply)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            if (FailAfterFirstChunk is not null) throw FailAfterFirstChunk;
        }

        yield return new ChatResponseUpdate { Contents = [new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 })] };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

public class AgentTests
{
    private static (StudyAgent agent, RecordingChatClient client) Create(
        LlmProvider provider = LlmProvider.Ollama, int historyTurns = 12)
    {
        var client = new RecordingChatClient();
        var llm = new LlmClient(client, provider, "test-model");
        var config = new StudyAgentOptions
        {
            SystemPrompt = "SYSTEM",
            SdaInstructions = "SDA-ON",
            CompareInstructions = "COMPARE-ON",
        };
        var agent = new StudyAgent(llm, new LlmConfig { HistoryTurns = historyTurns, OllamaTemperature = 0.2f },
            new ToolRegistry(), config, NullLogger.Instance);
        return (agent, client);
    }

    [Fact]
    public async Task Answer_IsStreamedInOrder_AndChatAsyncJoinsIt()
    {
        var (agent, _) = Create();

        var chunks = new List<string>();
        await foreach (var c in agent.ChatStreamAsync("hi")) chunks.Add(c);

        Assert.Equal(["Hello", " there"], chunks);
        Assert.Equal("Hello there", await agent.ChatAsync("again"));
    }

    [Fact]
    public async Task UserMessage_PutsInstructionsAndMaterialBeforeTheQuestion()
    {
        var (agent, client) = Create();

        await agent.ChatAsync("What is grace?", ragContext: "REFERENCE MATERIAL:\n[S1] x", includeSDA: true, mode: QueryMode.Compare);

        var user = client.Calls[0].Last(m => m.Role == ChatRole.User).Text;
        Assert.True(user.IndexOf("SDA-ON") < user.IndexOf("REFERENCE MATERIAL"));
        Assert.True(user.IndexOf("COMPARE-ON") < user.IndexOf("REFERENCE MATERIAL"));
        Assert.True(user.IndexOf("REFERENCE MATERIAL") < user.IndexOf("User question: What is grace?"));
    }

    [Fact]
    public async Task SystemPrompt_NeverChanges_WhateverTheSwitches()
    {
        var (agent, client) = Create();

        await agent.ChatAsync("one", includeSDA: false);
        await agent.ChatAsync("two", includeSDA: true, mode: QueryMode.Compare);

        Assert.All(client.Calls, call =>
        {
            var system = Assert.Single(call, m => m.Role == ChatRole.System);
            Assert.Equal("SYSTEM", system.Text);
        });
    }

    [Fact]
    public async Task NoAssistantReminderIsInjected_SoTheRequestEndsWithTheUserTurn()
    {
        var (agent, client) = Create();

        await agent.ChatAsync("q", ragContext: "material");

        Assert.Equal(ChatRole.User, client.Calls[0].Last().Role);
        Assert.DoesNotContain(client.Calls[0], m => m.Role == ChatRole.Assistant);
    }

    [Fact]
    public async Task Material_IsDroppedFromHistoryAfterTheAnswer()
    {
        var (agent, client) = Create();

        await agent.ChatAsync("first question", ragContext: "HUGE MATERIAL");
        await agent.ChatAsync("follow up");

        var second = client.Calls[1];
        Assert.DoesNotContain(second, m => m.Text.Contains("HUGE MATERIAL"));
        Assert.Contains(second, m => m.Role == ChatRole.User && m.Text == "first question");
        Assert.Contains(second, m => m.Role == ChatRole.Assistant && m.Text == "Hello there");
    }

    [Fact]
    public async Task History_KeepsOnlyTheLastNExchanges()
    {
        var (agent, client) = Create(historyTurns: 2);

        for (var i = 1; i <= 4; i++) await agent.ChatAsync($"q{i}");

        var last = client.Calls[3].Where(m => m.Role == ChatRole.User).Select(m => m.Text).ToList();
        Assert.Equal(["q2", "q3", "q4"], last); // the two kept exchanges plus the new turn
        Assert.Single(client.Calls[3], m => m.Role == ChatRole.System);
    }

    [Fact]
    public async Task FailedTurn_LeavesNoDanglingUserMessage()
    {
        var (agent, client) = Create();
        client.FailAfterFirstChunk = new InvalidOperationException("boom");

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.ChatAsync("will fail"));
        client.FailAfterFirstChunk = null;
        await agent.ChatAsync("works");

        Assert.DoesNotContain(client.Calls[1], m => m.Text.Contains("will fail"));
    }

    [Fact]
    public async Task Reset_StartsOver()
    {
        var (agent, client) = Create();
        await agent.ChatAsync("before");

        agent.Reset();
        await agent.ChatAsync("after");

        Assert.DoesNotContain(client.Calls[1], m => m.Text.Contains("before"));
    }

    [Fact]
    public async Task Ollama_GetsTemperatureAndTools_ButHostedProvidersGetNeitherTemperature()
    {
        var (ollama, ollamaClient) = Create(LlmProvider.Ollama);
        var (anthropic, anthropicClient) = Create(LlmProvider.Anthropic);

        await ollama.ChatAsync("q");
        await anthropic.ChatAsync("q");

        Assert.Equal(0.2f, ollamaClient.Options[0]!.Temperature);
        Assert.Null(anthropicClient.Options[0]!.Temperature);   // sampling parameters are rejected by current Claude models
    }

    [Theory]
    [InlineData(LlmProvider.Anthropic, true)]
    [InlineData(LlmProvider.OpenAI, true)]
    [InlineData(LlmProvider.Ollama, false)]
    [InlineData(LlmProvider.ClaudeCode, false)]   // takes --effort on its command line instead
    public async Task Effort_IsSentToTheApiProviders_Only(LlmProvider provider, bool expected)
    {
        var (agent, client) = Create(provider);

        await agent.ChatAsync("q");

        Assert.Equal(expected ? ReasoningEffort.Medium : null, client.Options[0]!.Reasoning?.Effort);
    }

    [Fact]
    public async Task ClaudeCode_GetsNoTools_AndAStableConversationId_ThatChangesOnReset()
    {
        var (agent, client) = Create(LlmProvider.ClaudeCode);

        await agent.ChatAsync("one");
        await agent.ChatAsync("two");
        agent.Reset();
        await agent.ChatAsync("three");

        Assert.All(client.Options, o => Assert.Null(o!.Tools));
        Assert.Equal(client.Options[0]!.ConversationId, client.Options[1]!.ConversationId);
        Assert.NotEqual(client.Options[1]!.ConversationId, client.Options[2]!.ConversationId);
    }

    [Fact]
    public async Task Anthropic_SystemPrompt_CarriesACacheBreakpoint()
    {
        var (agent, client) = Create(LlmProvider.Anthropic);
        ChatMessage? seen = null;
        var spy = new SpyClient(client, m => seen ??= m.First(x => x.Role == ChatRole.System));
        var llm = new LlmClient(spy, LlmProvider.Anthropic, "m");
        var cached = new StudyAgent(llm, new LlmConfig(), new ToolRegistry(),
            new StudyAgentOptions { SystemPrompt = "SYSTEM" }, NullLogger.Instance);

        await cached.ChatAsync("q");

        Assert.NotNull(seen);
        var content = Assert.Single(seen!.Contents);
        Assert.Contains("anthropic:cache_control", content.AdditionalProperties?.Keys ?? []);
    }

    private sealed class SpyClient(IChatClient inner, Action<IEnumerable<ChatMessage>> onCall) : DelegatingChatClient(inner)
    {
        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            onCall(messages);
            return base.GetStreamingResponseAsync(messages, options, cancellationToken);
        }
    }
}

public class TranscriptBuilderTests
{
    private static ChatMessage User(string question, string? material = null) => new(ChatRole.User, material ?? question)
    {
        AdditionalProperties = new() { [TranscriptBuilder.QuestionProperty] = question },
    };

    [Fact]
    public void UsesThePlainQuestion_NotTheRetrievedMaterial()
    {
        var text = TranscriptBuilder.Condense([
            User("What is grace?", "REFERENCE MATERIAL: lots of text\n\nUser question: What is grace?"),
            new ChatMessage(ChatRole.Assistant, "Grace is unmerited favour."),
        ]);

        Assert.Contains("User: What is grace?", text);
        Assert.Contains("Assistant: Grace is unmerited favour.", text);
        Assert.DoesNotContain("REFERENCE MATERIAL", text);
    }

    [Fact]
    public void OlderExchanges_AreSummarisedInOneLine_RecentOnesKeptInFull()
    {
        var messages = new List<ChatMessage>();
        for (var i = 1; i <= 6; i++)
        {
            messages.Add(User($"question {i}"));
            messages.Add(new ChatMessage(ChatRole.Assistant, $"answer {i} " + new string('x', 400)));
        }

        var text = TranscriptBuilder.Condense(messages, fullTurns: 2);

        Assert.Contains("- Q: question 1 → A: answer 1", text);                 // brief
        Assert.DoesNotContain("User: question 1", text);
        Assert.Contains("User: question 6", text);                              // full
        Assert.Contains(new string('x', 400), text.Split("Assistant: answer 6")[1]);
    }

    [Fact]
    public void ToolMessagesAndEmptyHistory_Produce_NoNoise()
    {
        Assert.Equal("", TranscriptBuilder.Condense([]));

        var text = TranscriptBuilder.Condense([
            User("q"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("1", "lookup_word")]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("1", "result")]),
            new ChatMessage(ChatRole.Assistant, "final"),
        ]);

        Assert.DoesNotContain("lookup_word", text);
        Assert.Contains("Assistant: final", text);
    }
}
