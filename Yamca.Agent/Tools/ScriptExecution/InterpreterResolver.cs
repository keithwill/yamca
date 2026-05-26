using System.Collections.Concurrent;

namespace Yamca.Agent.Tools.ScriptExecution;

/// <summary>
/// Resolves interpreter names (e.g. "pwsh", "python3") to absolute executable paths by
/// walking the process <c>PATH</c> (and <c>PATHEXT</c> on Windows). Results are cached
/// per instance for the lifetime of the session — PATH does not change mid-run.
/// </summary>
public sealed class InterpreterResolver
{
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the absolute path of the first matching executable on PATH,
    /// or null if not found. Names are tried in order; the first hit wins.</summary>
    public string? Resolve(IEnumerable<string> candidateNames)
    {
        ArgumentNullException.ThrowIfNull(candidateNames);
        foreach (var name in candidateNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var resolved = _cache.GetOrAdd(name, FindOnPath);
            if (resolved is not null) return resolved;
        }
        return null;
    }

    private static string? FindOnPath(string executableName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        var directories = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var extensions = GetExtensions(executableName);

        foreach (var dir in directories)
        {
            var trimmed = dir.Trim().Trim('"');
            if (trimmed.Length == 0) continue;

            foreach (var ext in extensions)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(trimmed, executableName + ext);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static string[] GetExtensions(string executableName)
    {
        if (!OperatingSystem.IsWindows())
            return new[] { string.Empty };

        // If the caller already supplied an extension, do not append PATHEXT entries.
        if (!string.IsNullOrEmpty(Path.GetExtension(executableName)))
            return new[] { string.Empty };

        var pathext = Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrWhiteSpace(pathext))
            return new[] { ".EXE", ".CMD", ".BAT", ".COM" };

        return pathext
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim())
            .Where(e => e.Length > 0)
            .ToArray();
    }
}
