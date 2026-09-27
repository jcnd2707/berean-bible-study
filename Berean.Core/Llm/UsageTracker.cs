using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Berean.Core.Llm;

/// <summary>
/// Logs the tokens each request used and keeps a running total per day, so the cost of a
/// hosted model (or the share of a subscription's limit, for Claude Code) is visible.
/// </summary>
public static class UsageTracker
{
    private static readonly object Gate = new();
    private static DateOnly _day = DateOnly.FromDateTime(DateTime.Now);
    private static long _input, _output, _cacheRead, _requests;

    public static (long Requests, long Input, long Output, long CacheRead) Today
    {
        get { lock (Gate) { Roll(); return (_requests, _input, _output, _cacheRead); } }
    }

    public static void Record(ILogger log, LlmClient llm, UsageDetails? usage, TimeSpan elapsed)
    {
        var input = usage?.InputTokenCount ?? 0;
        var output = usage?.OutputTokenCount ?? 0;
        long cacheRead = 0;
        usage?.AdditionalCounts?.TryGetValue("CacheReadInputTokens", out cacheRead);

        (long Requests, long Input, long Output, long CacheRead) total;
        lock (Gate)
        {
            Roll();
            _requests++; _input += input; _output += output; _cacheRead += cacheRead;
            total = (_requests, _input, _output, _cacheRead);
        }

        log.LogInformation(
            "[Usage] {Provider}/{Model}: in={In} out={Out} cacheRead={Cache} in {Sec:F1}s | today: {Req} request(s), in={TIn} out={TOut}",
            llm.Provider, llm.Model, input, output, cacheRead, elapsed.TotalSeconds,
            total.Requests, total.Input, total.Output);
    }

    private static void Roll()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today == _day) return;
        _day = today;
        _input = _output = _cacheRead = _requests = 0;
    }
}
