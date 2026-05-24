using OpenAI.Chat;

namespace Yamca.Agent.Tools;

public interface IToolRegistry
{
    IReadOnlyList<ITool> Tools { get; }

    ITool? Get(string name);

    /// <summary>OpenAI ChatTool definitions to send with a chat completion request.</summary>
    IReadOnlyList<ChatTool> GetChatTools();
}
