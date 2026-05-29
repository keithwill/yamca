namespace Yamca.Agent.Tools.CodeIntel;

/// <summary>
/// Shared limits for the symbol extractors' container recursion.
/// </summary>
public static class SymbolDepth
{
    /// <summary>
    /// Deepest nesting level (1-indexed) at which a symbol is still emitted. Extractors
    /// recurse into a container's body only while the current depth is below this value, so
    /// a container that lands exactly at <see cref="MaxContainerDepth"/> is listed but its
    /// members/nested types are not. This caps the output's token cost for small-context
    /// local models; note that a leading <c>package</c>/<c>namespace</c> declaration consumes
    /// one level. Raise it (or remove the gate) to surface deeper nesting.
    /// </summary>
    public const int MaxContainerDepth = 3;
}
