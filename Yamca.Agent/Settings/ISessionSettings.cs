namespace Yamca.Agent.Settings;

/// <summary>
/// Live view of the user's settings for the current Blazor circuit. The web layer
/// hydrates this from localStorage on page load and updates it on changes.
/// </summary>
public interface ISessionSettings
{
    ToolSettingsMap Project { get; }
    ToolSettingsMap Global { get; }

    EndpointSettings Endpoint { get; }

    /// <summary>System prompt for new chat sessions. Kept stable across sessions so
    /// upstream prompt caching can reuse it; per-session context like the workspace
    /// path is appended by <see cref="Chat.ChatSession"/> as a separate message.</summary>
    string SystemPrompt { get; }
}
