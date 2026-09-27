using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Berean.Core.Tools;

/// <summary>
/// Owns all tool definitions and records every invocation.
/// Register tools here; the agent picks them all up automatically.
/// </summary>
public class ToolRegistry
{
    private readonly List<AITool> _tools = [];
    private readonly List<ToolResult> _invocations = [];

    public IReadOnlyList<AITool> Tools => _tools;
    public IReadOnlyList<ToolResult> Invocations => _invocations;

    /// <summary>Raised just before a tool runs: (tool name, arguments as JSON). Used to show "Looking up hesed…".</summary>
    public event Action<string, string>? ToolStarted;

    // ── Registration ───────────────────────────────────────────────────────

    public void Register(Delegate handler, string? name = null)
    {
        var fn = AIFunctionFactory.Create(handler, name);
        _tools.Add(new TrackedTool(fn, _invocations, name ?? fn.Name, (n, a) => ToolStarted?.Invoke(n, a)));
    }

    // ── Tracked wrapper ────────────────────────────────────────────────────

    private sealed class TrackedTool : AIFunction
    {
        private readonly AIFunction _inner;
        private readonly List<ToolResult> _log;
        private readonly string _name;
        private readonly Action<string, string> _started;

        public override string Name => _name;
        public override string Description => _inner.Description;

        // The wrapper must expose the wrapped function's parameter schema: the model only knows
        // what arguments a tool takes from it (hosted models reject a tool without one).
        public override JsonElement JsonSchema => _inner.JsonSchema;
        public override JsonElement? ReturnJsonSchema => _inner.ReturnJsonSchema;
        public override JsonSerializerOptions JsonSerializerOptions => _inner.JsonSerializerOptions;
        public override MethodInfo? UnderlyingMethod => _inner.UnderlyingMethod;
        public override IReadOnlyDictionary<string, object?> AdditionalProperties => _inner.AdditionalProperties;

        public TrackedTool(AIFunction inner, List<ToolResult> log, string name, Action<string, string> started)
        {
            _inner = inner;
            _log = log;
            _name = name;
            _started = started;
        }

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var args = JsonSerializer.Serialize(arguments.ToDictionary(k => k.Key, v => v.Value));
            _started(_name, args);

            try
            {
                var result = await _inner.InvokeAsync(arguments, cancellationToken);
                lock (_log)
                    _log.Add(new ToolResult { ToolName = _name, Arguments = args, Result = result?.ToString() ?? "(null)" });
                return result;
            }
            catch (ArgumentException ex) when (ex.Message.Contains("missing a value for the required parameter"))
            {
                var errorMsg = $"Tool call failed: required parameter missing. {ex.Message} " +
                               "Please retry and include all required parameters.";

                lock (_log)
                    _log.Add(new ToolResult { ToolName = _name, Arguments = args, Result = errorMsg });

                return errorMsg; // returned to the model as a tool result — it can recover
            }
        }
    }
}
