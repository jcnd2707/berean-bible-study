using System.Text.Json;

// FakeClaude stands in for `claude -p` in tests. It records how it was called, keeps a
// list of "sessions" on disk, and prints canned stream-json like the real CLI does.
//
//   FAKE_CLAUDE_LOG    file that gets one JSON line per call: { args, prompt, cwd }
//   FAKE_CLAUDE_STATE  directory holding one empty file per known session id
//
// Prompts containing SLEEP hang; prompts containing FAIL exit with code 3 and a stderr message.

var argList = args.ToList();
var prompt = Console.In.ReadToEnd();

var log = Environment.GetEnvironmentVariable("FAKE_CLAUDE_LOG");
if (log is not null)
    File.AppendAllText(log, JsonSerializer.Serialize(new { args = argList, prompt, cwd = Directory.GetCurrentDirectory() }) + "\n");

string? Value(string flag)
{
    var i = argList.IndexOf(flag);
    return i >= 0 && i + 1 < argList.Count ? argList[i + 1] : null;
}

void Print(object o) { Console.Out.WriteLine(JsonSerializer.Serialize(o)); Console.Out.Flush(); }

var state = Environment.GetEnvironmentVariable("FAKE_CLAUDE_STATE") ?? Path.GetTempPath();
var newSession = Value("--session-id");
var resume = Value("--resume");

if (resume is not null && !File.Exists(Path.Combine(state, resume)))
{
    Print(new
    {
        type = "result", subtype = "error_during_execution", is_error = true,
        errors = new[] { $"No conversation found with session ID: {resume}" },
        session_id = Guid.NewGuid().ToString(),
        usage = new { input_tokens = 0, output_tokens = 0 },
    });
    return 1;
}

if (newSession is not null) File.WriteAllText(Path.Combine(state, newSession), "");
var id = resume ?? newSession ?? Guid.NewGuid().ToString();

if (prompt.Contains("SLEEP")) Thread.Sleep(60_000);
if (prompt.Contains("FAIL"))
{
    Console.Error.WriteLine("kaboom");
    return 3;
}

Print(new { type = "system", subtype = "init", session_id = id, tools = Array.Empty<string>() });

var first = prompt.Split('\n').Last(l => l.Trim().Length > 0).Trim();
var reply = "Echo: " + (first.Length > 30 ? first[..30] : first);
foreach (var piece in new[] { reply[..6], reply[6..] })
    Print(new
    {
        type = "stream_event",
        session_id = id,
        @event = new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = piece } },
    });

Print(new
{
    type = "result", subtype = "success", is_error = false, result = reply, session_id = id,
    usage = new { input_tokens = 10, output_tokens = 5, cache_read_input_tokens = 3, cache_creation_input_tokens = 2 },
});
return 0;
