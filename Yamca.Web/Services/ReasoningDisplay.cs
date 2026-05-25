namespace Yamca.Web.Services;

/// <summary>How the chat should render reasoning / chain-of-thought blocks
/// extracted from inline <c>&lt;think&gt;</c>-style tags.</summary>
public enum ReasoningDisplay
{
    /// <summary>Do not render reasoning at all.</summary>
    Hidden,
    /// <summary>Show a small disclosure header; the user clicks to expand.
    /// While streaming, the block stays open and auto-collapses when finished.</summary>
    Collapsed,
    /// <summary>Always render reasoning expanded.</summary>
    Expanded,
}
