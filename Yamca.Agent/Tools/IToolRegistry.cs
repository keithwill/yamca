using Yamca.Agent.Chat;

namespace Yamca.Agent.Tools;

public interface IToolRegistry
{
    IReadOnlyList<ITool> Tools { get; }

    ITool? Get(string name);

    /// <summary>ChatTool definitions to send with a chat completion request: the Eager tools
    /// (including the mandatory-eager <c>lookup_tool</c>/<c>call_tool</c> meta-tools). Deferred
    /// and Hidden tools are never included — deferred tools are invoked through <c>call_tool</c>
    /// so their schemas stay out of the prompt prefix (preserving the prefix cache).</summary>
    IReadOnlyList<ChatTool> GetChatTools(IAvailabilityResolver availability);

    /// <summary>Tools whose effective availability is <see cref="Availability.Deferred"/>.
    /// Used by <c>lookup_tool</c> to advertise and describe loadable tools.
    /// Hidden tools are never returned — that is what distinguishes Hidden from Deferred.</summary>
    IReadOnlyList<ITool> GetDeferredTools(IAvailabilityResolver availability);

    /// <summary>Tools to display in the settings permissions table.</summary>
    IReadOnlyList<ITool> GetSettingsTools();
}
