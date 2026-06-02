namespace Yamca.Agent.Tools;

public interface IAvailabilityResolver
{
    /// <summary>
    /// Resolve the effective availability for a tool by merging
    /// project → user → <see cref="ITool.DefaultAvailability"/>,
    /// then clamping with the tool's <see cref="ITool.MandatoryEager"/> /
    /// <see cref="ITool.CanBeHidden"/> flags.
    /// </summary>
    Availability Resolve(string toolName);
}
