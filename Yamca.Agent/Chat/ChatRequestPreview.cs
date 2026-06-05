namespace Yamca.Agent.Chat;

/// <summary>A point-in-time snapshot of what the next LLM request for a chat session would
/// contain. Built by <see cref="AgentLoop.BuildRequestPreview"/> for the "view raw context"
/// diagnostic.</summary>
/// <param name="SystemPromptBase">The base system-prompt portion (<see cref="ChatSession.SystemPrompt"/>),
/// used to separate the author-supplied prompt from the appended workspace line, instruction
/// files, and compaction summary when rendering the single system message. On a restored
/// session this equals the entire system message.</param>
/// <param name="Messages">The exact messages array that would be sent.</param>
/// <param name="Tools">The exact tools array that would be sent.</param>
/// <param name="RawJson">The serialized request body, or <c>null</c> when the underlying client
/// does not support <see cref="IChatRequestPreview"/>.</param>
public sealed record ChatRequestPreview(
    string SystemPromptBase,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ChatTool> Tools,
    string? RawJson);
