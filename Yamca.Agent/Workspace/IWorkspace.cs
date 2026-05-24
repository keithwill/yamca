namespace Yamca.Agent.Workspace;

public interface IWorkspace
{
    string RootPath { get; }

    string Resolve(string requestedPath);
}
