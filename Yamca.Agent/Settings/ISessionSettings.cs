namespace Yamca.Agent.Settings;

/// <summary>
/// Live view of the user's settings for the current Blazor circuit. The web layer
/// hydrates this from disk on page load and updates it on changes.
/// </summary>
public interface ISessionSettings
{
    ToolSettingsMap Project { get; }
    ToolSettingsMap Global { get; }

    EndpointsSettings Endpoints { get; }

    /// <summary>System prompt for new chat sessions. Kept stable across sessions so
    /// upstream prompt caching can reuse it; per-session context like the workspace
    /// path is appended by <see cref="Chat.ChatSession"/> as a separate message.</summary>
    string SystemPrompt { get; }

    /// <summary>Project-tier script registry. Empty when no scripts are registered
    /// for the current workspace.</summary>
    ScriptRegistry ProjectScripts { get; }

    /// <summary>Global-tier script registry — applies to every workspace.</summary>
    ScriptRegistry GlobalScripts { get; }

    /// <summary>Project-tier subagent registry. Empty when none are configured for the
    /// current workspace. Merged with <see cref="GlobalSubagents"/> at the use site, with
    /// project entries overriding global entries of the same name.</summary>
    SubagentRegistry ProjectSubagents { get; }

    /// <summary>Global-tier subagent registry — applies to every workspace. Seeded with a
    /// few low-risk built-ins on first run.</summary>
    SubagentRegistry GlobalSubagents { get; }

    /// <summary>Controls how much of the deferred-tool catalog is included in the frozen
    /// session-start system message. Honored by <c>lookup_tool</c>'s session-start contribution.</summary>
    DeferredToolsHint DeferredToolsHint { get; }

    /// <summary>Maximum LLM round-trips per turn. Used as the default iteration cap for a
    /// subagent run when the subagent does not override it.</summary>
    int MaxToolIterations { get; }
}
