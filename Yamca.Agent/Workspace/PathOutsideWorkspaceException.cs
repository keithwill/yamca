namespace Yamca.Agent.Workspace;

public sealed class PathOutsideWorkspaceException : Exception
{
    public string RequestedPath { get; }
    public string ResolvedPath { get; }
    public string RootPath { get; }

    public PathOutsideWorkspaceException(string requestedPath, string resolvedPath, string rootPath)
        : base($"Path '{requestedPath}' resolves to '{resolvedPath}', which is outside workspace root '{rootPath}'.")
    {
        RequestedPath = requestedPath;
        ResolvedPath = resolvedPath;
        RootPath = rootPath;
    }
}
