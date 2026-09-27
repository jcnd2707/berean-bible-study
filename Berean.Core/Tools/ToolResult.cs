namespace Berean.Core.Tools;

public class ToolResult
{
    public required string ToolName { get; init; }
    public required string Arguments { get; init; }
    public required string Result { get; init; }
    public DateTime CalledAt { get; init; } = DateTime.UtcNow;
}
