using Berean.Agent.Api.Services;
using Xunit;

namespace Berean.Core.Tests;

public class SessionLimitsTests
{
    private static readonly SessionLimits Limits = new()
    {
        MaxQuestions = 12,
        WarnQuestionsLeft = 3,
        MaxContextTokens = 60000,
        WarnContextFraction = 0.8,
    };

    [Theory]
    [InlineData(0, 0, "ok")]
    [InlineData(8, 30000, "ok")]
    [InlineData(9, 30000, "nearing")]   // 12 - 3 = 9 questions left triggers "nearing"
    [InlineData(8, 48000, "nearing")]   // 60000 * 0.8 = 48000
    [InlineData(12, 30000, "full")]     // question limit reached
    [InlineData(8, 60000, "full")]      // context limit reached
    [InlineData(15, 70000, "full")]     // both over
    public void Evaluate_ClassifiesEachRangeCorrectly(int questions, int contextTokens, string expected)
    {
        var state = Limits.Evaluate(questions, contextTokens);

        Assert.Equal(expected, state.State);
        Assert.Equal(questions, state.QuestionsUsed);
        Assert.Equal(contextTokens, state.ContextTokens);
        Assert.Equal(Limits.MaxQuestions, state.MaxQuestions);
        Assert.Equal(Limits.MaxContextTokens, state.MaxContextTokens);
    }

    [Fact]
    public void Evaluate_FullWinsOverNearing_WhicheverLimitIsHitFirst()
    {
        // Over on questions but nowhere near the context limit — still full.
        Assert.Equal("full", Limits.Evaluate(12, 0).State);
        // Over on context but nowhere near the question limit — still full.
        Assert.Equal("full", Limits.Evaluate(0, 60000).State);
    }
}
