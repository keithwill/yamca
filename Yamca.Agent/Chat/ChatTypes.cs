namespace Yamca.Agent.Chat;

public enum ChatRole { System, User, Assistant, Tool }

/// <summary>One entry in a chat conversation. <see cref="ToolCallId"/> is set only
/// for <see cref="ChatRole.Tool"/> messages; <see cref="ToolCalls"/> only for
/// <see cref="ChatRole.Assistant"/> messages that requested tool execution.</summary>
public sealed record ChatMessage(
    ChatRole Role,
    string Content,
    string? ToolCallId = null,
    IReadOnlyList<ChatToolCall>? ToolCalls = null);

public sealed record ChatToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>A tool definition advertised to the model. <see cref="ParametersJsonSchema"/>
/// is a raw JSON Schema document.</summary>
public sealed record ChatTool(string Name, string Description, string ParametersJsonSchema);
