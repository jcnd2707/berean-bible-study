using System.Collections.Concurrent;

namespace Berean.Agent.Api.Services;

/// <summary>
/// Tracks the one answer that may be running on a SignalR connection at a time, so it can be
/// cancelled from a different hub method call (the Stop button — D11 in PR1_QUICK_WINS_PLAN.md).
///
/// <see cref="MaximumParallelInvocationsPerClient"/> is raised to 2 so <c>CancelMessage</c> isn't
/// queued behind the <c>SendMessage</c> it's meant to stop — but that also means a hub method that
/// switches conversations (<c>StartConversation</c>, <c>ResumeConversation</c>,
/// <c>ContinueConversation</c>, <c>DeleteConversation</c>) can now run concurrently with a
/// streaming <c>SendMessage</c> on the same connection. Cancelling isn't instant: the running
/// answer needs an await point to observe it. <see cref="WaitForIdleAsync"/> closes that window —
/// a conversation-switching method awaits it after calling <see cref="Cancel"/>, so the old
/// answer's <c>TokenReceived</c>/<c>MessageStopped</c> sends are guaranteed to have gone out
/// before the new conversation's <c>ConversationStarted</c>/<c>ConversationLoaded</c> does. Without
/// that, the two could interleave on the wire — the client has no message/conversation id to tell
/// a stale streaming chunk from a current one.
/// </summary>
public sealed class ActiveAnswers
{
    private sealed record Entry(CancellationTokenSource Cts, TaskCompletionSource Done);

    private readonly ConcurrentDictionary<string, Entry> _active = new();
    private readonly ILogger<ActiveAnswers> _log;

    public ActiveAnswers(ILogger<ActiveAnswers> log)
    {
        _log = log;
    }

    /// <summary>
    /// Starts tracking a running answer for this connection, linked to <paramref name="aborted"/>
    /// (the connection's own <c>ConnectionAborted</c> token) so a dropped connection cancels it
    /// too. Null if an answer is already running on this connection.
    /// </summary>
    public CancellationToken? TryBegin(string connectionId, CancellationToken aborted)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        var entry = new Entry(cts, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        if (_active.TryAdd(connectionId, entry)) return cts.Token;

        cts.Dispose();
        return null;
    }

    /// <summary>Cancels the running answer, if any. Returns false if nothing was running.</summary>
    public bool Cancel(string connectionId)
    {
        if (!_active.TryGetValue(connectionId, out var entry)) return false;
        entry.Cts.Cancel();
        return true;
    }

    /// <summary>
    /// Waits until the answer running on this connection (if any) has finished sending everything
    /// it's going to send — see the type doc. A no-op if nothing is running. Never throws; a
    /// timeout just means the caller proceeds without the ordering guarantee, logged so it's
    /// visible if it ever actually happens.
    /// </summary>
    public async Task WaitForIdleAsync(string connectionId, TimeSpan timeout)
    {
        if (!_active.TryGetValue(connectionId, out var entry)) return;

        var finished = await Task.WhenAny(entry.Done.Task, Task.Delay(timeout));
        if (finished != entry.Done.Task)
            _log.LogWarning("[ActiveAnswers] {ConnId} — the previous answer didn't stop within {Timeout}; proceeding anyway",
                connectionId, timeout);
    }

    /// <summary>Marks the answer as finished. Call in the <c>finally</c> around the streaming loop.</summary>
    public void End(string connectionId)
    {
        if (!_active.TryRemove(connectionId, out var entry)) return;
        entry.Done.TrySetResult();
        entry.Cts.Dispose();
    }
}
