using Berean.Agent.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Berean.Core.Tests;

public sealed class ActiveAnswersTests
{
    private static ActiveAnswers Make() => new(NullLogger<ActiveAnswers>.Instance);

    [Fact]
    public void TryBegin_ASecondBeginWhileOneIsRunning_IsRefused()
    {
        var answers = Make();
        Assert.NotNull(answers.TryBegin("conn1", CancellationToken.None));

        Assert.Null(answers.TryBegin("conn1", CancellationToken.None));
    }

    [Fact]
    public void Cancel_CancelsTheToken()
    {
        var answers = Make();
        var token = answers.TryBegin("conn1", CancellationToken.None)!.Value;

        Assert.True(answers.Cancel("conn1"));
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_WithNothingRunning_ReturnsFalse()
    {
        Assert.False(Make().Cancel("conn1"));
    }

    [Fact]
    public void End_LetsANewAnswerStart()
    {
        var answers = Make();
        answers.TryBegin("conn1", CancellationToken.None);
        Assert.Null(answers.TryBegin("conn1", CancellationToken.None)); // still running

        answers.End("conn1");

        Assert.NotNull(answers.TryBegin("conn1", CancellationToken.None)); // free again
    }

    [Fact]
    public async Task WaitForIdleAsync_ReturnsAsSoonAsEndIsCalled()
    {
        var answers = Make();
        answers.TryBegin("conn1", CancellationToken.None);

        var wait = answers.WaitForIdleAsync("conn1", TimeSpan.FromSeconds(5));
        Assert.False(wait.IsCompleted);

        answers.End("conn1");

        var completed = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(wait, completed);
    }

    [Fact]
    public async Task WaitForIdleAsync_WithNothingRunning_ReturnsImmediately()
    {
        // Must not hang — nothing will ever call End() for a connection that never began.
        var wait = Make().WaitForIdleAsync("conn1", TimeSpan.FromSeconds(5));
        var completed = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(wait, completed);
    }
}
