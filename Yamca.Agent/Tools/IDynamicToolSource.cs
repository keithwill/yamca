namespace Yamca.Agent.Tools;

/// <summary>
/// A live source of tools that may appear/disappear between turns (e.g. MCP
/// servers connecting on first use, or reconnecting after a config change).
/// <see cref="ToolRegistry"/> queries every registered source every time it
/// builds a tool list, so callers don't have to rebuild DI to pick up new
/// tools.
/// </summary>
public interface IDynamicToolSource
{
    IReadOnlyList<ITool> CurrentTools { get; }
}
