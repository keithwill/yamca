using Yamca.Agent.Chat;

namespace Yamca.Agent.Tools;

public interface IToolRegistry
{
    IReadOnlyList<ITool> Tools { get; }

    ITool? Get(string name);

    /// <summary>ChatTool definitions to send with a chat completion request. Includes all
    /// Eager tools plus any Deferred tools that have been loaded for this session.
    /// Hidden tools are never included.</summary>
    IReadOnlyList<ChatTool> GetChatTools(LoadedToolSet loaded, IAvailabilityResolver availability);

    /// <summary>Tools whose effective availability is <see cref="Availability.Deferred"/>,
    /// regardless of load state. Used by <c>load_tool</c> to advertise loadable names.
    /// Hidden tools are never returned — that is what distinguishes Hidden from Deferred.</summary>
    IReadOnlyList<ITool> GetDeferredTools(IAvailabilityResolver availability);

    /// <summary>Tools to display in the settings permissions table.</summary>
    IReadOnlyList<ITool> GetSettingsTools();
}
