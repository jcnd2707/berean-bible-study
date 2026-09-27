using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Berean.Core.Llm;

/// <summary>
/// An <see cref="IChatClient"/> backed by the Claude Code CLI in headless mode (<c>claude -p</c>).
/// It uses the Claude login already on this machine — no API key, no per-token bill; usage counts
/// against the subscription's limits instead. Personal use on your own machine only.
///
/// One process is started per message. The prompt goes in through stdin (long command lines are
/// fragile on Windows) and the answer streams back as JSON lines.
///
/// The CLI is a coding agent, so it is locked down to plain text generation: no built-in tools,
/// no MCP servers, no skills, no user/project settings, and an empty working directory.
///
/// Conversation state: the agent's own message list is the source of truth and the Claude Code
/// session is a cache of it. The first message starts a session (<c>--session-id</c>); later ones
/// send only the new message and <c>--resume</c> it. When a resume isn't possible — the session
/// no longer exists, or the caller trimmed/reset the history — a fresh session starts with a
/// condensed transcript of the earlier messages, and the new session id is remembered.
/// </summary>
public sealed class ClaudeCodeChatClient : IChatClient
{
    private readonly ClaudeCodeConfig _cfg;
    private readonly string _model;
    private readonly ILogger _log;

    // conversation key (ChatOptions.ConversationId) → the CLI session that mirrors it
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new();
    private string? _executable;

    /// <param name="ExpectedPriorCount">
    /// How many messages the caller should have before the next user message if nothing was
    /// trimmed: the previous user message and the assistant reply were the last two.
    /// </param>
    private sealed record SessionState(string SessionId, int ExpectedPriorCount);

    public ClaudeCodeChatClient(ClaudeCodeConfig config, string model, ILogger log)
    {
        _cfg = config;
        _model = model;
        _log = log;
    }

    // ── IChatClient ────────────────────────────────────────────────────────

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in GetStreamingResponseAsync(messages, options, cancellationToken))
            updates.Add(u);
        return updates.ToChatResponse();
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var lastUser = list.FindLastIndex(m => m.Role == ChatRole.User);
        if (lastUser < 0) throw new ArgumentException("There is no user message to send.", nameof(messages));

        var system = string.Join("\n\n", list.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
        var userText = list[lastUser].Text;
        var key = options?.ConversationId ?? "default";

        if (options?.Tools is { Count: > 0 })
            _log.LogWarning("[ClaudeCode] {Count} tool(s) were offered but Claude Code runs without tools; ignoring them.",
                options.Tools.Count);

        var resume = _sessions.TryGetValue(key, out var state) && state.ExpectedPriorCount == lastUser;
        if (!resume && state is not null)
            _log.LogInformation("[ClaudeCode] History changed ({Expected} expected, {Actual} present) — starting a new session",
                state.ExpectedPriorCount, lastUser);

        while (true)
        {
            var sessionId = resume ? state!.SessionId : Guid.NewGuid().ToString();
            var prompt = resume ? userText : WithTranscript(list, lastUser, userText);

            var sawText = false;
            ResultEvent? result = null;

            await foreach (var ev in RunAsync(system, prompt, sessionId, resume, cancellationToken))
            {
                if (ev.Text is not null)
                {
                    sawText = true;
                    yield return new ChatResponseUpdate(ChatRole.Assistant, ev.Text)
                    {
                        ConversationId = sessionId,
                        ModelId = _model,
                    };
                }
                else if (ev.Result is not null)
                {
                    result = ev.Result;
                }
            }

            if (result is null)
                throw new InvalidOperationException("Claude Code ended without a result.");

            if (result.IsError)
            {
                // The session was deleted (retention period) or never existed on this machine.
                if (resume && !sawText && result.Message.Contains("No conversation found", StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogWarning("[ClaudeCode] Session {Id} is gone — restarting with a condensed transcript", sessionId);
                    resume = false;
                    continue;
                }
                throw new InvalidOperationException($"Claude Code failed: {result.Message}");
            }

            _sessions[key] = new SessionState(sessionId, lastUser + 2);

            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                FinishReason = ChatFinishReason.Stop,
                ConversationId = sessionId,
                ModelId = _model,
                Contents = [new UsageContent(result.Usage)],
            };
            yield break;
        }
    }

    /// <summary>The CLI session currently mirroring a conversation, so it can be stored.</summary>
    public string? SessionIdFor(string conversationKey) =>
        _sessions.TryGetValue(conversationKey, out var s) ? s.SessionId : null;

    /// <summary>
    /// Points a conversation back at a stored CLI session (after a restart or reconnect).
    /// <paramref name="messageCount"/> is how many messages the caller has before the next user
    /// message. If the session has since been deleted, the next message falls back to a new
    /// session with a condensed transcript.
    /// </summary>
    public void RestoreSession(string conversationKey, string sessionId, int messageCount) =>
        _sessions[conversationKey] = new SessionState(sessionId, messageCount);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    // ── Prompt building ────────────────────────────────────────────────────

    private static string WithTranscript(List<ChatMessage> messages, int lastUser, string userText)
    {
        var prior = messages.Take(lastUser).Where(m => m.Role != ChatRole.System).ToList();
        var transcript = TranscriptBuilder.Condense(prior);
        return transcript.Length == 0
            ? userText
            : $"{transcript}\n\n[New message from the user]\n{userText}";
    }

    // ── Process ────────────────────────────────────────────────────────────

    private abstract record RunEvent
    {
        public string? Text => (this as TextEvent)?.Value;
        public ResultEvent? Result => this as ResultEvent;
    }
    private sealed record TextEvent(string Value) : RunEvent;
    private sealed record ResultEvent(bool IsError, string Message, UsageDetails Usage) : RunEvent;

    private IEnumerable<string> BuildArgs(string system, string sessionId, bool resume)
    {
        yield return "-p";
        yield return "--output-format"; yield return "stream-json";
        yield return "--verbose";
        yield return "--include-partial-messages";

        // Lock down: text in, text out.
        yield return "--tools"; yield return "";                 // no built-in tools
        yield return "--strict-mcp-config";                      // ignore the user's MCP servers
        yield return "--permission-mode"; yield return "dontAsk";
        yield return "--disable-slash-commands";                 // no skills
        yield return "--setting-sources"; yield return "";       // no user/project settings or CLAUDE.md

        yield return "--model"; yield return _model;
        if (!string.IsNullOrWhiteSpace(_cfg.Effort)) { yield return "--effort"; yield return _cfg.Effort; }
        if (!string.IsNullOrWhiteSpace(system)) { yield return "--system-prompt"; yield return system; }

        yield return resume ? "--resume" : "--session-id";
        yield return sessionId;
    }

    private async IAsyncEnumerable<RunEvent> RunAsync(
        string system, string prompt, string sessionId, bool resume,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolveExecutable(),
            WorkingDirectory = EnsureWorkingDirectory(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in BuildArgs(system, sessionId, resume)) psi.ArgumentList.Add(arg);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _cfg.TimeoutSeconds)));

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };

        _log.LogInformation("[ClaudeCode] {Mode} session {Id} ({Chars} chars)", resume ? "resuming" : "starting", sessionId, prompt.Length);
        process.Start();
        process.BeginErrorReadLine();

        // Kill the process if the caller cancels or the timeout fires.
        using var killOnCancel = timeout.Token.Register(() => Kill(process));

        var sawText = false;
        try
        {
            await process.StandardInput.WriteAsync(prompt.AsMemory(), CancellationToken.None);
            process.StandardInput.Close();

            string? line;
            while ((line = await ReadLineAsync(process.StandardOutput, timeout.Token, ct)) is not null)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                var ev = Parse(line);
                if (ev is null) continue;

                if (ev is TextEvent) sawText = true;
                // Without partial messages the answer only arrives in the final result.
                if (ev is ResultEvent { IsError: false } r && !sawText && r.Message.Length > 0)
                    yield return new TextEvent(r.Message);
                yield return ev;
            }

            // Killing the process (just above, on cancel or timeout) can close its stdout pipe
            // before the pending read notices the cancellation, so the loop exits on a plain EOF
            // (null) that races ahead of ReadLineAsync's own exception. Checking the tokens
            // directly here makes the outcome deterministic either way: a user cancellation must
            // still surface as OperationCanceledException, not fall through to "ended without a
            // result", and likewise a timeout must surface as TimeoutException.
            if (timeout.Token.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException($"Claude Code did not finish within {_cfg.TimeoutSeconds}s.");
            }

            await process.WaitForExitAsync(CancellationToken.None);
            if (process.ExitCode != 0 && !sawText)
            {
                string err; lock (stderr) err = stderr.ToString().Trim();
                if (err.Length > 0) throw new InvalidOperationException($"Claude Code exited with code {process.ExitCode}: {err}");
            }
        }
        finally
        {
            Kill(process);
        }
    }

    /// <summary>Reads a line, turning "the timeout fired" into a TimeoutException (a user cancel stays a cancel).</summary>
    private async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken timeoutToken, CancellationToken userToken)
    {
        try { return await reader.ReadLineAsync(timeoutToken); }
        catch (OperationCanceledException) when (!userToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Claude Code did not finish within {_cfg.TimeoutSeconds}s.");
        }
    }

    private RunEvent? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "system" when root.TryGetProperty("subtype", out var sub) && sub.GetString() == "init":
                    if (root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0)
                        _log.LogWarning("[ClaudeCode] The session reports {Count} tool(s) — expected none", tools.GetArrayLength());
                    return null;

                case "stream_event":
                    var ev = root.GetProperty("event");
                    if (ev.GetProperty("type").GetString() == "content_block_delta" &&
                        ev.GetProperty("delta").TryGetProperty("type", out var dt) && dt.GetString() == "text_delta")
                        return new TextEvent(ev.GetProperty("delta").GetProperty("text").GetString() ?? "");
                    return null;

                case "result":
                    return ParseResult(root);

                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ResultEvent ParseResult(JsonElement root)
    {
        var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;

        string message = "";
        if (isError && root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            message = string.Join("; ", errors.EnumerateArray().Select(x => x.GetString() ?? ""));
        if (message.Length == 0 && root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String)
            message = r.GetString() ?? "";
        if (isError && message.Length == 0)
            message = root.TryGetProperty("subtype", out var st) ? st.GetString() ?? "error" : "error";

        var usage = new UsageDetails();
        if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            long Get(string name) => u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
            var cacheRead = Get("cache_read_input_tokens");
            var cacheWrite = Get("cache_creation_input_tokens");
            usage.InputTokenCount = Get("input_tokens") + cacheRead + cacheWrite;
            usage.OutputTokenCount = Get("output_tokens");
            usage.TotalTokenCount = usage.InputTokenCount + usage.OutputTokenCount;
            usage.AdditionalCounts = new AdditionalPropertiesDictionary<long>
            {
                ["CacheReadInputTokens"] = cacheRead,
                ["CacheCreationInputTokens"] = cacheWrite,
            };
        }

        return new ResultEvent(isError, message, usage);
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    // ── Locating claude ────────────────────────────────────────────────────

    private string EnsureWorkingDirectory()
    {
        var dir = string.IsNullOrWhiteSpace(_cfg.WorkingDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BereanAgent", "claude-workdir")
            : _cfg.WorkingDirectory;
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string ResolveExecutable()
    {
        if (_executable is not null) return _executable;

        var configured = string.IsNullOrWhiteSpace(_cfg.ExecutablePath) ? "claude" : _cfg.ExecutablePath;
        var found = File.Exists(configured) ? Path.GetFullPath(configured) : FindOnPath(configured);
        if (found is null)
            throw new FileNotFoundException(
                $"Could not find '{configured}'. Install Claude Code and log in, or set Llm:ClaudeCode:ExecutablePath to the full path of claude.exe.");

        // On Windows an npm install leaves a claude.cmd shim, which can't take a multi-line
        // --system-prompt argument. The real claude.exe sits next to it.
        if (found.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || found.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            var exe = Path.Combine(Path.GetDirectoryName(found)!, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
            if (!File.Exists(exe))
                throw new FileNotFoundException(
                    $"'{found}' is a script shim and claude.exe was not found next to it. Set Llm:ClaudeCode:ExecutablePath to the full path of claude.exe.");
            found = exe;
        }

        _log.LogInformation("[ClaudeCode] Using {Path}", found);
        return _executable = found;
    }

    private static string? FindOnPath(string name)
    {
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        // Prefer a real executable over a shim anywhere on PATH.
        foreach (var ext in extensions)
            foreach (var dir in dirs)
            {
                var candidate = Path.Combine(dir.Trim('"'), name + ext);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }
}
