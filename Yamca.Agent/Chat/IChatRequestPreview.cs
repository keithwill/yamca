namespace Yamca.Agent.Chat;

/// <summary>Optional capability for a <see cref="IChatCompletionClient"/> that can serialize
/// the exact request body it would POST — without sending it. Used by the "view raw context"
/// diagnostic so the displayed payload can never drift from the wire format. Implemented by the
/// production client only; test fakes simply don't implement it (the diagnostic falls back to a
/// "raw JSON unavailable" placeholder).</summary>
public interface IChatRequestPreview
{
    /// <summary>Serialize the request body that <see cref="IChatCompletionClient.StreamAsync"/>
    /// would send for the given messages and tools, using the same options.</summary>
    string SerializeRequest(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools);
}
