namespace Yamca.Agent.Settings;

/// <summary>How much of the deferred-tool catalog is folded into the frozen session-start
/// system message. Trades initial-context size (every token here is paid on every turn for the
/// whole session) against how readily the model discovers that a deferred tool exists. The
/// dispatcher's <c>lookup_tool</c> always advertises the live catalog on demand regardless of
/// this setting; this only governs the up-front hint.</summary>
public enum DeferredToolsHint
{
    /// <summary>No per-tool catalog in the session-start message — but the model is still told
    /// that deferred tools exist and can be loaded via <c>lookup_tool</c>/<c>call_tool</c>.
    /// Smallest context; the model learns which specific tools exist only by calling
    /// <c>lookup_tool</c>.</summary>
    None,

    /// <summary>List deferred tool names only — enough of a cue to prompt a lookup, with minimal
    /// token cost. Default.</summary>
    Names,

    /// <summary>List deferred tool names with their one-line descriptions. Most discoverable,
    /// largest up-front context cost.</summary>
    NamesAndDescriptions,
}
