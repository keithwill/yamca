namespace Yamca.Agent.Permissions;

public enum PermissionLevel
{
    /// <summary>Runs without prompting.</summary>
    Allow,

    /// <summary>Internal-only — a transient "refused this call" signal, never a stored
    /// setting. Produced when an <see cref="Allow"/>/<see cref="Ask"/> approval is rejected,
    /// and consumed in the same call to refuse execution. It is deliberately NOT offered as a
    /// user-selectable permission: forbidding a tool outright is expressed as
    /// <see cref="Tools.Availability.Hidden"/> instead (the model never sees a hidden tool, so
    /// it cannot waste a turn calling something that can only ever be denied). Any legacy
    /// persisted <c>Deny</c> is migrated to Hidden on load.</summary>
    Deny,

    /// <summary>Pauses for the user's approval before each call.</summary>
    Ask
}
