using Yamca.Agent.Chat;

namespace Yamca.Agent.Tools;

public interface IToolRegistry
{
    IReadOnlyList<ITool> Tools { get; }

    ITool? Get(string name);

    /// <summary>ChatTool definitions to send with a chat completion request. Includes all
    /// non-deferred tools plus any deferred tools that have been loaded for this session.</summary>
    IReadOnlyList<ChatTool> GetChatTools(LoadedToolSet loaded);

    /// <summary>All tools marked <see cref="ITool.Deferred"/>, regardless of load state.
    /// Used by <c>load_tool</c> to advertise available names and validate load requests.</summary>
    IReadOnlyList<ITool> GetDeferredTools();

    /// <summary>Tools to display in the settings permissions table.</summary>
    IReadOnlyList<ITool> GetSettingsTools();
}
