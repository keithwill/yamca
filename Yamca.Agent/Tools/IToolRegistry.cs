using Yamca.Agent.Chat;

namespace Yamca.Agent.Tools;

public interface IToolRegistry
{
    IReadOnlyList<ITool> Tools { get; }

    ITool? Get(string name);

    /// <summary>ChatTool definitions to send with a chat completion request.</summary>
    IReadOnlyList<ChatTool> GetChatTools();
}
