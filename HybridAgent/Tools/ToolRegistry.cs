using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using HybridAgent.Core.Models;

namespace HybridAgent.Core.Tools;

/// <summary>
/// Owns all tool definitions and tracks every invocation for the DiagnosisSummary.
/// Register tools here; the DiagnosticAgent picks them all up automatically.
/// </summary>
public class ToolRegistry
{
    private readonly List<AITool> _tools = [];
    private readonly List<ToolResult> _invocations = [];

    public IReadOnlyList<AITool> Tools => _tools;
    public IReadOnlyList<ToolResult> Invocations => _invocations;

    // ── Registration ───────────────────────────────────────────────────────

    public void Register(Delegate handler, string? name = null)
    {
        var fn = AIFunctionFactory.Create(handler, name);
        _tools.Add(new TrackedTool(fn, _invocations, name ?? fn.Name));
    }

    // ── Default tools ──────────────────────────────────────────────────────

    public void RegisterDefaults()
    {
        Register(
            [Description("Returns the current local date and time.")]
        () => DateTime.Now.ToString("f"),
            "get_current_datetime"
        );

        Register(
            [Description("Returns basic information about the local system (OS, CPU count, memory).")]
        () =>
            {
                var mem = GC.GetGCMemoryInfo();
                return $"OS: {Environment.OSVersion} | CPUs: {Environment.ProcessorCount} | " +
                       $"Available RAM: {mem.TotalAvailableMemoryBytes / 1024 / 1024} MB";
            },
            "get_system_info"
        );

        Register(
            [Description("Evaluates a basic arithmetic expression and returns the result.")]
        ([Description("Math expression to evaluate, e.g. '12 * 4 + 7'")] string expression) =>
            {
                try
                {
                    var table = new System.Data.DataTable();
                    return table.Compute(expression, null)?.ToString() ?? "null";
                }
                catch (Exception ex) { return $"Error: {ex.Message}"; }
            },
            "calculate"
        );

        Register(
            [Description("Checks whether a given hostname or URL is reachable from this machine.")]
        async ([Description("Hostname or URL to check, e.g. 'google.com'")] string host) =>
            {
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var url = host.StartsWith("http") ? host : $"https://{host}";
                    var resp = await client.GetAsync(url);
                    return $"{host} is reachable — HTTP {(int)resp.StatusCode}";
                }
                catch { return $"{host} is NOT reachable (timeout or DNS failure)"; }
            },
            "check_host"
        );

        Register(
            [Description("Reads the value of an environment variable. Returns (not set) if missing.")]
        ([Description("Name of the environment variable")] string name) =>
                Environment.GetEnvironmentVariable(name) ?? "(not set)",
            "read_env_var"
        );
    }

    // ── Tracked wrapper ────────────────────────────────────────────────────

    private sealed class TrackedTool : AIFunction
    {
        private readonly AIFunction _inner;
        private readonly List<ToolResult> _log;
        private readonly string _name;

        // AITool requires Name and Description to be overridden
        public override string Name => _name;
        public override string Description => _inner.Description;

        public TrackedTool(AIFunction inner, List<ToolResult> log, string name)
        {
            _inner = inner;
            _log = log;
            _name = name;
        }

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var result = await _inner.InvokeAsync(arguments, cancellationToken);

            _log.Add(new ToolResult
            {
                ToolName = _name,
                Arguments = JsonSerializer.Serialize(
                    arguments.ToDictionary(k => k.Key, v => v.Value)),
                Result = result?.ToString() ?? "(null)"
            });

            return result;
        }
    }
}