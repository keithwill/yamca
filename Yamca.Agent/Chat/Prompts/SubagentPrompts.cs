namespace Yamca.Agent.Chat.Prompts;

/// <summary>Static text fragments that compose a subagent's system message. The headless
/// preamble leads every subagent run, forming a byte-stable prefix that prefix-caching
/// inference servers can reuse regardless of which subagent's per-run instructions follow.
/// Keep <see cref="HeadlessPreamble"/> byte-stable: it is a <c>const</c> precisely so it is
/// defined once and never composed at runtime.</summary>
public static class SubagentPrompts
{
    /// <summary>Fixed preamble prepended (followed by a blank line) to every subagent's
    /// system prompt. Byte-stable by contract — the shared prefix for prefix caching.</summary>
    public const string HeadlessPreamble =
        "You are running headless as a subagent — there is no user to talk to. When you " +
        "have finished, you MUST call the subagent_result tool exactly once with your complete " +
        "answer and a status (success, failure, or needs_followup); that is the only output the " +
        "caller receives, and the run is not complete until you call it. If the task tells you " +
        "to \"reply\", \"answer\", \"output\", or \"print\" something, deliver that through " +
        "subagent_result rather than writing it as a message — the result tool is the only " +
        "channel the caller can read. Do not ask clarifying questions: make reasonable " +
        "assumptions and proceed. Your responses are not rendered as Markdown.";

    /// <summary>Fallback instructions used when a subagent definition supplies none.</summary>
    public const string DefaultInstructions =
        "You are a focused subagent that completes a single delegated task.";
}
