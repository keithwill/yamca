namespace Yamca.Agent.Tools;

public sealed record ToolResult(bool IsError, string Content)
{
    public static ToolResult Ok(string content) => new(false, content);
    public static ToolResult Error(string message) => new(true, message);
}
