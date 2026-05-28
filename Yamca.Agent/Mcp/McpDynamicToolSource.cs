using Yamca.Agent.Tools;

namespace Yamca.Agent.Mcp;

/// <summary>Adapts <see cref="IMcpRegistry"/> to <see cref="IDynamicToolSource"/>
/// so MCP-provided tools merge into the main <see cref="ToolRegistry"/> at
/// query time.</summary>
public sealed class McpDynamicToolSource : IDynamicToolSource
{
    private readonly IMcpRegistry _registry;

    public McpDynamicToolSource(IMcpRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    public IReadOnlyList<ITool> CurrentTools => _registry.Tools;
}
