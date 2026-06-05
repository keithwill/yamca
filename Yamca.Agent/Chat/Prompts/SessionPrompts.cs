namespace Yamca.Agent.Chat.Prompts;

/// <summary>Static text fragments that compose the parent chat's system message, plus the
/// <see cref="SystemSection"/> markers used to delimit its component sections. The base
/// system prompt itself is user-authored (a setting) and so is not a constant here; what
/// lives here is the fixed framing yamca adds around it. The single source of truth for the
/// section markers is shared by the writers that emit them and the "view raw context" reader
/// that parses them back out.</summary>
public static class SessionPrompts
{
    /// <summary>Appended to the user-authored system prompt when Markdown rendering is on.</summary>
    public const string MarkdownHint =
        "Your responses are rendered as GitHub-flavored Markdown — use fenced code blocks for code, and standard Markdown for emphasis, lists, and tables.";

    /// <summary>Appended to the user-authored system prompt when Markdown rendering is off.</summary>
    public const string PlainTextHint =
        "Your responses are rendered as plain text. Do NOT use Markdown formatting: no `backticks`, no **bold**/*italics*, no #headings, no fenced code blocks, no bullet/numbered lists. Write code and identifiers inline as plain text.";

    /// <summary>The workspace line appended after the base prompt: "Current workspace: &lt;path&gt;".</summary>
    public static readonly SystemSection Workspace = new("Current workspace: ", "Workspace");

    /// <summary>The header that prefixes each loaded instruction file's body:
    /// "# Instructions from &lt;relative-path&gt;".</summary>
    public static readonly SystemSection InstructionFile = new("# Instructions from ", "Instruction file");

    /// <summary>The compaction summary block folded into the system message during
    /// auto-compaction: "[Summary of earlier conversation]: &lt;summary&gt;".</summary>
    public static readonly SystemSection CompactionSummary = new("[Summary of earlier conversation]: ", "Compaction summary");
}
