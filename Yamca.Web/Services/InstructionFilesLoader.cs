using Yamca.Agent.Chat.Prompts;
using Yamca.Agent.Workspace;

namespace Yamca.Web.Services;

/// <summary>Reads the configured instruction files at session start and returns
/// pre-formatted system-message strings ready to append to a chat session.
/// Missing files and IO failures are skipped silently.</summary>
public sealed class InstructionFilesLoader
{
    private const int MaxFileBytes = 256 * 1024;

    public IReadOnlyList<string> Load(SessionSettings settings, IWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(workspace);

        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (settings.ProjectInheritsUserInstructions)
            AddPaths(ordered, seen, settings.UserInstructionFiles);

        AddPaths(ordered, seen, settings.ProjectInstructionFiles);

        var messages = new List<string>(ordered.Count);
        foreach (var relative in ordered)
        {
            var content = TryRead(workspace, relative);
            if (content is null) continue;
            messages.Add($"{SessionPrompts.InstructionFile.Marker}{relative}\n\n{content}");
        }
        return messages;
    }

    private static void AddPaths(List<string> ordered, HashSet<string> seen, IReadOnlyList<string> source)
    {
        foreach (var raw in source)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var normalized = raw.Trim().Replace('\\', '/');
            if (Path.IsPathRooted(normalized)) continue;
            if (!seen.Add(normalized)) continue;
            ordered.Add(normalized);
        }
    }

    private static string? TryRead(IWorkspace workspace, string relative)
    {
        try
        {
            var absolute = workspace.Resolve(relative);
            var info = new FileInfo(absolute);
            if (!info.Exists) return null;
            if (info.Length > MaxFileBytes) return null;
            return File.ReadAllText(absolute);
        }
        catch
        {
            return null;
        }
    }
}
