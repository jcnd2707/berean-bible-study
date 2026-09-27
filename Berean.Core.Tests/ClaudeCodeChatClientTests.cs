using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Berean.Core.Tests;

/// <summary>
/// The Claude Code client against a fake claude executable (tests-support/FakeClaude), so the
/// process handling, lock-down flags and session logic are tested without using a subscription.
/// The fake is steered through environment variables, so these tests must not run in parallel.
/// </summary>
[Collection("FakeClaude")]
public sealed class ClaudeCodeChatClientTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"berean-cc-{Guid.NewGuid():N}");
    private readonly string _log;
    private readonly string _work;

    public ClaudeCodeChatClientTests()
    {
        Directory.CreateDirectory(_dir);
        _log = Path.Combine(_dir, "calls.jsonl");
        _work = Path.Combine(_dir, "workdir");
        Environment.SetEnvironmentVariable("FAKE_CLAUDE_LOG", _log);
        Environment.SetEnvironmentVariable("FAKE_CLAUDE_STATE", _dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("FAKE_CLAUDE_LOG", null);
        Environment.SetEnvironmentVariable("FAKE_CLAUDE_STATE", null);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static string FakeClaudePath()
    {
        var name = OperatingSystem.IsWindows() ? "FakeClaude.exe" : "FakeClaude";
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var root = Path.Combine(dir.FullName, "tests-support", "FakeClaude", "bin");
            if (!Directory.Exists(root)) continue;
            var found = Directory.EnumerateFiles(root, name, SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (found is not null) return found;
        }
        throw new FileNotFoundException("Build tests-support/FakeClaude first (dotnet build tests-support/FakeClaude).");
    }

    private ClaudeCodeChatClient Client(int timeoutSeconds = 30) => new(
        new ClaudeCodeConfig { ExecutablePath = FakeClaudePath(), WorkingDirectory = _work, TimeoutSeconds = timeoutSeconds, Effort = "medium" },
        "opus", NullLogger.Instance);

    private sealed record Call(List<string> Args, string Prompt, string Cwd)
    {
        public string? After(string flag) { var i = Args.IndexOf(flag); return i >= 0 ? Args[i + 1] : null; }
    }

    private List<Call> Calls() => File.Exists(_log)
        ? File.ReadAllLines(_log).Select(l =>
        {
            using var doc = JsonDocument.Parse(l);
            var r = doc.RootElement;
            return new Call(r.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToList(),
                r.GetProperty("prompt").GetString()!, r.GetProperty("cwd").GetString()!);
        }).ToList()
        : [];

    private static async Task<(string text, ChatResponseUpdate? usage)> Stream(
        ClaudeCodeChatClient c, List<ChatMessage> messages, string conversation = "c1")
    {
        var text = "";
        ChatResponseUpdate? usage = null;
        await foreach (var u in c.GetStreamingResponseAsync(messages, new ChatOptions { ConversationId = conversation }))
        {
            text += string.Concat(u.Contents.OfType<TextContent>().Select(t => t.Text));
            if (u.Contents.OfType<UsageContent>().Any()) usage = u;
        }
        return (text, usage);
    }

    private static ChatMessage Sys(string t = "SYSTEM PROMPT\nwith two lines") => new(ChatRole.System, t);
    private static ChatMessage User(string t) => new(ChatRole.User, t);
    private static ChatMessage Bot(string t) => new(ChatRole.Assistant, t);

    // ── tests ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task FirstMessage_StartsASession_AndStreamsTheAnswer()
    {
        var (text, usage) = await Stream(Client(), [Sys(), User("Hello there")]);

        Assert.Equal("Echo: Hello there", text);
        var u = usage!.Contents.OfType<UsageContent>().Single().Details;
        Assert.Equal(15, u.InputTokenCount);      // 10 + cache read 3 + cache write 2
        Assert.Equal(5, u.OutputTokenCount);
        Assert.Equal(3, u.AdditionalCounts!["CacheReadInputTokens"]);

        var call = Assert.Single(Calls());
        Assert.Equal("Hello there", call.Prompt);
        Assert.NotNull(call.After("--session-id"));
        Assert.Null(call.After("--resume"));
        Assert.Equal("opus", call.After("--model"));
        Assert.Equal("medium", call.After("--effort"));
    }

    [Fact]
    public async Task IsLockedDown_NoToolsNoMcpNoSkillsNoSettings_AndTheSystemPromptIsPassedIntact()
    {
        await Stream(Client(), [Sys(), User("hi")]);

        var call = Assert.Single(Calls());
        Assert.Equal("", call.After("--tools"));                    // empty list = no built-in tools
        Assert.Contains("--strict-mcp-config", call.Args);
        Assert.Contains("--disable-slash-commands", call.Args);
        Assert.Equal("", call.After("--setting-sources"));           // no user/project settings or CLAUDE.md
        Assert.Equal("dontAsk", call.After("--permission-mode"));
        Assert.Equal("SYSTEM PROMPT\nwith two lines", call.After("--system-prompt"));
        Assert.Contains("-p", call.Args);
        Assert.DoesNotContain("--bare", call.Args);                  // --bare would disable the subscription login
        Assert.DoesNotContain("--dangerously-skip-permissions", call.Args);
    }

    [Fact]
    public async Task RunsInTheConfiguredWorkingDirectory_WhichIsCreated()
    {
        await Stream(Client(), [Sys(), User("hi")]);

        Assert.True(Directory.Exists(_work));
        Assert.Equal(Path.GetFullPath(_work), Path.GetFullPath(Calls().Single().Cwd));
    }

    [Fact]
    public async Task FollowUp_ResumesTheSession_AndSendsOnlyTheNewMessage()
    {
        var c = Client();
        await Stream(c, [Sys(), User("first question")]);

        await Stream(c, [Sys(), User("first question"), Bot("Echo: first question"), User("second question")]);

        var calls = Calls();
        Assert.Equal(2, calls.Count);
        Assert.Equal(calls[0].After("--session-id"), calls[1].After("--resume"));
        Assert.Equal("second question", calls[1].Prompt);
    }

    [Fact]
    public async Task ConversationsAreIndependent()
    {
        var c = Client();
        await Stream(c, [Sys(), User("a")], conversation: "one");
        await Stream(c, [Sys(), User("b")], conversation: "two");

        var calls = Calls();
        Assert.NotEqual(calls[0].After("--session-id"), calls[1].After("--session-id"));
        Assert.All(calls, x => Assert.Null(x.After("--resume")));
    }

    [Fact]
    public async Task TrimmedHistory_StartsANewSession_WithACondensedTranscript()
    {
        var c = Client();
        await Stream(c, [Sys(), User("q1"), ]);
        await Stream(c, [Sys(), User("q1"), Bot("Echo: q1"), User("q2")]);

        // The caller trimmed the oldest exchange: the message count no longer matches the session.
        await Stream(c, [Sys(), User("q2"), Bot("Echo: q2"), User("q3")]);

        var calls = Calls();
        Assert.Null(calls[2].After("--resume"));
        Assert.NotNull(calls[2].After("--session-id"));
        Assert.Contains("[Earlier in this conversation", calls[2].Prompt);
        Assert.Contains("User: q2", calls[2].Prompt);
        Assert.EndsWith("q3", calls[2].Prompt.Trim());
    }

    [Fact]
    public async Task AVanishedSession_IsReplacedTransparently_WithATranscript()
    {
        var c = Client();
        await Stream(c, [Sys(), User("q1")]);
        foreach (var f in Directory.GetFiles(_dir).Where(f => !f.EndsWith(".jsonl"))) File.Delete(f); // "retention" deleted the session

        var (text, _) = await Stream(c, [Sys(), User("q1"), Bot("Echo: q1"), User("q2")]);

        Assert.Equal("Echo: q2", text);
        var calls = Calls();
        Assert.Equal(3, calls.Count);                                   // start, failed resume, fresh start
        Assert.NotNull(calls[1].After("--resume"));
        Assert.Null(calls[2].After("--resume"));
        Assert.Contains("User: q1", calls[2].Prompt);
        Assert.NotEqual(calls[0].After("--session-id"), calls[2].After("--session-id"));

        // …and the new session is remembered, so the next message resumes it.
        await Stream(c, [Sys(), User("q1"), Bot("Echo: q1"), User("q2"), Bot("Echo: q2"), User("q3")]);
        Assert.Equal(calls[2].After("--session-id"), Calls()[3].After("--resume"));
    }

    [Fact]
    public async Task ToolsOfferedByTheCaller_AreIgnored()
    {
        var c = Client();
        var options = new ChatOptions { ConversationId = "c", Tools = [AIFunctionFactory.Create(() => "x", "t")] };

        await foreach (var _ in c.GetStreamingResponseAsync([Sys(), User("hi")], options)) { }

        Assert.DoesNotContain("t", Calls().Single().Args);
    }

    [Fact]
    public async Task GetResponseAsync_CollectsTheStream()
    {
        var response = await Client().GetResponseAsync([Sys(), User("Hello there")], new ChatOptions { ConversationId = "x" });

        Assert.Equal("Echo: Hello there", response.Text);
        Assert.Equal(5, response.Usage!.OutputTokenCount);
    }

    [Fact]
    public async Task ANonZeroExit_SurfacesTheErrorOutput()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Stream(Client(), [Sys(), User("please FAIL")]));

        Assert.Contains("kaboom", ex.Message);
    }

    [Fact]
    public async Task ATimeout_KillsTheProcess_AndSaysSo()
    {
        var c = Client(timeoutSeconds: 1);

        await Assert.ThrowsAsync<TimeoutException>(() => Stream(c, [Sys(), User("SLEEP")]));
    }

    [Fact]
    public async Task Cancelling_StopsTheRun()
    {
        var c = Client();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in c.GetStreamingResponseAsync([Sys(), User("SLEEP")], new ChatOptions { ConversationId = "z" }, cts.Token)) { }
        });
    }

    [Fact]
    public async Task ARequestWithoutAUserMessage_IsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in Client().GetStreamingResponseAsync([Sys()])) { }
        });
    }
}

[CollectionDefinition("FakeClaude", DisableParallelization = true)]
public sealed class FakeClaudeCollection;
