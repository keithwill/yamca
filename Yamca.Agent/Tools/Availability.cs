namespace Yamca.Agent.Tools;

/// <summary>
/// Controls whether a tool's schema is sent to the LLM, reached on demand via the
/// <c>lookup_tool</c>/<c>call_tool</c> dispatcher, or suppressed entirely. The effective value is resolved per call by
/// <c>IAvailabilityResolver</c> walking Project → User → tool default, then clamped
/// by the tool's <see cref="ITool.MandatoryEager"/> / <see cref="ITool.CanBeHidden"/> flags.
/// </summary>
public enum Availability
{
    /// <summary>Listed in the initial tool schema sent to the model every turn.</summary>
    Eager,

    /// <summary>Not in the initial schema. Discoverable and described via <c>lookup_tool</c>,
    /// then invoked through <c>call_tool</c> — the schema never enters the prompt prefix.</summary>
    Deferred,

    /// <summary>Completely invisible to the LLM. Not listed by <c>lookup_tool</c>, not callable
    /// even by name. Internal code paths (<c>IToolRegistry.Get</c>) can still resolve the tool —
    /// Hidden is about model visibility, not capability removal.</summary>
    Hidden,
}
