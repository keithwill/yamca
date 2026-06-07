namespace Yamca.Agent.Tools.Git;

/// <summary>The curated set of git subcommands the <c>git</c> tool will run, split into a
/// read-only tier (cannot mutate the repo regardless of the args the model supplies, so it
/// resolves under <c>git_read</c> — default Allow) and a mutating tier (resolves under
/// <c>git_write</c> — default Ask). Anything outside this list is rejected and the model is
/// told to fall back to <c>execute_command</c>.</summary>
internal static class GitSubcommands
{
    public static readonly IReadOnlySet<string> Read = new HashSet<string>(StringComparer.Ordinal)
        { "status", "log", "diff", "show", "blame" };

    public static readonly IReadOnlySet<string> Write = new HashSet<string>(StringComparer.Ordinal)
        { "add", "restore", "commit", "switch", "branch", "stash", "fetch", "pull", "push" };

    /// <summary>True when <paramref name="op"/> is curated; <paramref name="isWrite"/> reports
    /// which permission tier it falls under.</summary>
    public static bool TryClassify(string op, out bool isWrite)
    {
        if (Read.Contains(op))  { isWrite = false; return true; }
        if (Write.Contains(op)) { isWrite = true;  return true; }
        isWrite = false;
        return false;
    }

    public static string ReadList  => string.Join(", ", Read);
    public static string WriteList => string.Join(", ", Write);
}
