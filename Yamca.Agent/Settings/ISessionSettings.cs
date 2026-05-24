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

    /// <summary>System prompt for new chat sessions. May contain the literal token
    /// <c>{{workspace}}</c>, which <see cref="Chat.ChatSession"/> substitutes with the
    /// workspace root path at session construction.</summary>
    string SystemPrompt { get; }
}
