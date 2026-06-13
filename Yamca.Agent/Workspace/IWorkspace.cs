namespace Yamca.Agent.Workspace;

public interface IWorkspace
{
    /// <summary>The sandbox boundary: the directory the session was opened to. All agent file
    /// operations are clamped to this path via <see cref="Resolve"/>.</summary>
    string RootPath { get; }

    /// <summary>The top-level directory of the git repository containing <see cref="RootPath"/>,
    /// or <see cref="RootPath"/> itself when not inside a repository. Repo-scoped artifacts — the
    /// shared document store (<c>.yamca/yamca.db</c>, holding the dev board) and worktrees
    /// (<c>.yamca/worktrees</c>) — anchor here so they
    /// resolve to the same place no matter which subdirectory the session was opened to. This is
    /// NOT a sandbox boundary and may sit above <see cref="RootPath"/>.</summary>
    string RepositoryRoot { get; }

    string Resolve(string requestedPath);
}
