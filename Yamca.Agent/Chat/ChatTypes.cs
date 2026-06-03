namespace Yamca.Agent.Chat;

public enum ChatRole { System, User, Assistant, Tool }

/// <summary>One entry in a chat conversation. <see cref="ToolCallId"/> is set only
/// for <see cref="ChatRole.Tool"/> messages; <see cref="ToolCalls"/> only for
/// <see cref="ChatRole.Assistant"/> messages that requested tool execution;
/// <see cref="Images"/> only for <see cref="ChatRole.User"/> messages that carry
/// attached image data. When images are present the message is sent to the model as
/// an OpenAI content-parts array (text part + one image_url part per image) rather
/// than a plain string.</summary>
public sealed record ChatMessage(
    ChatRole Role,
    string Content,
    string? ToolCallId = null,
    IReadOnlyList<ChatToolCall>? ToolCalls = null,
    IReadOnlyList<ChatImage>? Images = null);

public sealed record ChatToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>An image attached to a user message. <see cref="Base64Data"/> is the raw
/// base64 (no <c>data:</c> prefix); the request serializer wraps it as a
/// <c>data:{MimeType};base64,{Base64Data}</c> URI for the <c>image_url</c> part.</summary>
public sealed record ChatImage(string MimeType, string Base64Data);

/// <summary>A tool definition advertised to the model. <see cref="ParametersJsonSchema"/>
/// is a raw JSON Schema document.</summary>
public sealed record ChatTool(string Name, string Description, string ParametersJsonSchema);
