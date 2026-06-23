namespace Yamca.Agent.Chat;

/// <summary>Tracks which deferred tools the LLM has loaded for the current session.
/// Owned per chat session (by <c>ChatViewModel</c>) and flowed into the agent loop and onto
/// <c>ToolContext</c>, rather than registered in DI — a DI scope spans a whole browser circuit,
/// which hosts several chat panes, and each pane needs its own set. Lookups are case-sensitive
/// (ordinal) to match how tool names are dispatched elsewhere.</summary>
public sealed class LoadedToolSet
{
    private readonly HashSet<string> _loaded = new(StringComparer.Ordinal);

    public bool Contains(string toolName) =>
        toolName is not null && _loaded.Contains(toolName);

    public bool MarkLoaded(string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        return _loaded.Add(toolName);
    }

    public IReadOnlyCollection<string> LoadedNames => _loaded;
}
