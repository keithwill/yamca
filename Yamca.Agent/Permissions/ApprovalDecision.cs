namespace Yamca.Agent.Permissions;

public enum ApprovalPersistence
{
    /// <summary>One-shot decision — do not persist.</summary>
    None,
    /// <summary>Save the decision into the project-tier tool settings.</summary>
    Project,
    /// <summary>Save the decision into the user-tier tool settings.</summary>
    User,
}

public sealed record ApprovalDecision(bool Approved, ApprovalPersistence Persistence);
