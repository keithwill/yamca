namespace Yamca.Agent.Permissions;

/// <summary>Persists an approval decision back to project- or user-tier settings
/// so subsequent calls to the same tool do not re-prompt. Implementations are
/// responsible for keeping <see cref="Settings.ISessionSettings"/> in sync so that
/// <see cref="IPermissionResolver"/> sees the new value immediately.</summary>
public interface IPermissionStore
{
    void Persist(string toolName, PermissionLevel decision, ApprovalPersistence tier);
}
