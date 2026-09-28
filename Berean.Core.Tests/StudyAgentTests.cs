using System.Runtime.CompilerServices;
using Berean.Core.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Berean.Core.Tests;

/// <summary>
/// StudyAgent against a fake IChatClient, for the parts of the Phase 5 session-limit machinery
/// that don't need a real model: the per-turn context size (largest request, not the sum or the
/// last) and the recap carry-over (first turn only).
/// </summary>
public sealed class StudyAgentTests
{
    private sealed class FakeChatClient : IChatClient
    {
        private readonly Queue<List<ChatResponseUpdate>> _responses = new();
        public List<List<ChatMessage>> Requests { get; } = [];

        public void Enqueue(List<ChatResponseUpdate> updates) => _responses.Enqueue(updates);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(messages.ToList());
            var updates = _responses.Count > 0 ? _responses.Dequeue() : [];
            foreach (var u in updates)
            {
                ct.ThrowIfCancellationRequested();
                yield return u;
                await Task.Yield();
            }
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            var updates = new List<ChatResponseUpdate>();
            await foreach (var u in GetStreamingResponseAsync(messages, options, ct)) updates.Add(u);
            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static ChatResponseUpdate Text(string t) => new(ChatRole.Assistant, t);
    private static ChatResponseUpdate Usage(long inputTokens) =>
        new() { Contents = [new UsageContent(new UsageDetails { InputTokenCount = inputTokens })] };

    private static StudyAgent MakeAgent(IChatClient client)
    {
        var llmClient = new LlmClient(client, LlmProvider.Ollama, "test-model");
        var config = new StudyAgentOptions { SystemPrompt = "SYSTEM" };
        return new StudyAgent(llmClient, new LlmConfig(), new ToolRegistry(), config, NullLogger.Instance);
    }

    [Fact]
    public async Task ContextTokens_IsTheLargestRequest_NotTheSumOrTheLast()
    {
        var fake = new FakeChatClient();
        fake.Enqueue([Text("part1"), Usage(20000), Text("part2"), Usage(35000), Text("answer"), Usage(8000)]);

        var agent = MakeAgent(fake);
        AgentTurn? captured = null;
        agent.TurnCompleted = t => { captured = t; return Task.CompletedTask; };

        await foreach (var _ in agent.ChatStreamAsync("question")) { }

        Assert.NotNull(captured);
        Assert.Equal(35000, captured!.ContextTokens); // not 63000 (sum) and not 8000 (last)
    }

    [Fact]
    public async Task ContextTokens_FallsBackToACharacterEstimate_WhenNoUsageIsReported()
    {
        const string answer = "answer, no usage content at all";
        var fake = new FakeChatClient();
        fake.Enqueue([Text(answer)]);

        var agent = MakeAgent(fake);
        AgentTurn? captured = null;
        agent.TurnCompleted = t => { captured = t; return Task.CompletedTask; };

        await foreach (var _ in agent.ChatStreamAsync("q1")) { }

        // At the point the estimate is taken, history is [system("SYSTEM"), user("q1"), assistant(answer)].
        var expectedChars = "SYSTEM".Length + "q1".Length + answer.Length;
        Assert.Equal(expectedChars / 4, captured!.ContextTokens);
    }

    [Fact]
    public async Task CarryOver_AppearsOnlyOnTheFirstTurn()
    {
        var fake = new FakeChatClient();
        fake.Enqueue([Text("a1")]);
        fake.Enqueue([Text("a2")]);

        var agent = MakeAgent(fake);
        agent.CarryOver = "Recap of the earlier part";

        await foreach (var _ in agent.ChatStreamAsync("q1")) { }
        await foreach (var _ in agent.ChatStreamAsync("q2")) { }

        var firstTurnUserMessage = fake.Requests[0].Last(m => m.Role == ChatRole.User);
        Assert.Contains("Recap of the earlier part", firstTurnUserMessage.Text);

        var secondTurnUserMessage = fake.Requests[1].Last(m => m.Role == ChatRole.User);
        Assert.DoesNotContain("Recap of the earlier part", secondTurnUserMessage.Text);
    }

    /// <summary>D11 (PR1_QUICK_WINS_PLAN.md): a stopped answer must never be half-saved — the
    /// dangling user message is rolled back and the turn never counts toward the question limit.</summary>
    [Fact]
    public async Task ACancelledTurn_LeavesHistoryUnchanged_AndDoesNotRaiseTurnCompleted()
    {
        var fake = new FakeChatClient();
        fake.Enqueue([Text("ok")]);

        var agent = MakeAgent(fake);
        var completedCalls = 0;
        agent.TurnCompleted = _ => { completedCalls++; return Task.CompletedTask; };

        await foreach (var _ in agent.ChatStreamAsync("first question")) { }
        var countAfterFirstTurn = agent.MessageCount;
        Assert.Equal(1, completedCalls);

        fake.Enqueue([Text("part1"), Text("part2")]);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in agent.ChatStreamAsync("second question", ct: cts.Token))
                cts.Cancel(); // cancel once the first chunk has arrived, before the next one
        });

        Assert.Equal(countAfterFirstTurn, agent.MessageCount); // no dangling user message left behind
        Assert.Equal(1, completedCalls);                       // TurnCompleted never fired for the stopped turn
    }
}
