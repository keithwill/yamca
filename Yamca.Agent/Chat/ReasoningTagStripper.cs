using System.Text;

namespace Yamca.Agent.Chat;

/// <summary>Streaming splitter that separates reasoning content (wrapped in tags
/// like <c>&lt;think&gt;...&lt;/think&gt;</c>) from the model's visible reply.
/// Stateful across calls because a tag can be split across two streamed chunks
/// (e.g. <c>"&lt;thi"</c> then <c>"nk&gt;"</c>).</summary>
public sealed class ReasoningTagStripper
{
    public static readonly IReadOnlyList<string> DefaultTags =
        new[] { "think", "thinking", "reasoning" };

    // Sanity cap on a pending tag candidate. A stray '<' in normal text (e.g. "1 < 2")
    // would otherwise stall the stream forever; once exceeded we flush as content.
    private const int MaxTagCandidate = 64;

    private readonly HashSet<string> _tags;
    private readonly StringBuilder _carry = new();
    private Mode _mode = Mode.Visible;

    public ReasoningTagStripper() : this(DefaultTags) { }

    public ReasoningTagStripper(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        _tags = new HashSet<string>(
            tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim().ToLowerInvariant()),
            StringComparer.Ordinal);
    }

    public readonly record struct Result(string Visible, string Reasoning, bool JustClosed);

    /// <summary>Feed the next streamed chunk and receive the split parts. Carries
    /// an incomplete tag forward to the next call.</summary>
    public Result Process(string? delta)
    {
        if (string.IsNullOrEmpty(delta) && _carry.Length == 0)
            return new Result(string.Empty, string.Empty, false);

        var visible = new StringBuilder();
        var reasoning = new StringBuilder();
        var justClosed = false;

        string buf;
        if (_carry.Length == 0) buf = delta ?? string.Empty;
        else
        {
            _carry.Append(delta);
            buf = _carry.ToString();
            _carry.Clear();
        }

        var i = 0;
        while (i < buf.Length)
        {
            var sink = _mode == Mode.Visible ? visible : reasoning;

            var lt = buf.IndexOf('<', i);
            if (lt < 0)
            {
                sink.Append(buf, i, buf.Length - i);
                break;
            }

            // Everything before '<' is plain content for the current mode.
            if (lt > i) sink.Append(buf, i, lt - i);

            // Look for the matching '>'.
            var gt = buf.IndexOf('>', lt + 1);
            if (gt < 0)
            {
                var remaining = buf.Length - lt;
                if (remaining > MaxTagCandidate)
                {
                    // Almost certainly not a tag — flush the '<' and keep scanning.
                    sink.Append('<');
                    i = lt + 1;
                    continue;
                }
                // Incomplete tag; carry forward for the next chunk.
                _carry.Append(buf, lt, remaining);
                return new Result(visible.ToString(), reasoning.ToString(), justClosed);
            }

            var tagBody = buf.AsSpan(lt + 1, gt - lt - 1);
            if (_mode == Mode.Visible)
            {
                if (IsOpenTag(tagBody, out var name) && _tags.Contains(name))
                {
                    _mode = Mode.Reasoning;
                    i = gt + 1;
                }
                else
                {
                    // Not a recognized open tag — emit verbatim.
                    visible.Append(buf, lt, gt - lt + 1);
                    i = gt + 1;
                }
            }
            else // Reasoning
            {
                if (IsCloseTag(tagBody, out var name) && _tags.Contains(name))
                {
                    _mode = Mode.Visible;
                    justClosed = true;
                    i = gt + 1;
                }
                else
                {
                    reasoning.Append(buf, lt, gt - lt + 1);
                    i = gt + 1;
                }
            }
        }

        return new Result(visible.ToString(), reasoning.ToString(), justClosed);
    }

    /// <summary>Flush any partial tag that never completed. Anything still buffered
    /// is emitted to the current mode's sink as-is.</summary>
    public Result Flush()
    {
        if (_carry.Length == 0) return new Result(string.Empty, string.Empty, false);
        var text = _carry.ToString();
        _carry.Clear();
        return _mode == Mode.Visible
            ? new Result(text, string.Empty, false)
            : new Result(string.Empty, text, false);
    }

    private enum Mode { Visible, Reasoning }

    private static bool IsOpenTag(ReadOnlySpan<char> body, out string name)
    {
        name = string.Empty;
        if (body.Length == 0 || body[0] == '/') return false;

        var end = 0;
        while (end < body.Length && IsTagNameChar(body[end])) end++;
        if (end == 0) return false;

        // Whatever follows must be end-of-tag or whitespace + attributes.
        if (end < body.Length && body[end] != ' ' && body[end] != '\t' && body[end] != '\r' && body[end] != '\n')
            return false;

        name = body[..end].ToString().ToLowerInvariant();
        return true;
    }

    private static bool IsCloseTag(ReadOnlySpan<char> body, out string name)
    {
        name = string.Empty;
        if (body.Length < 2 || body[0] != '/') return false;

        var inner = body[1..].TrimEnd();
        var end = 0;
        while (end < inner.Length && IsTagNameChar(inner[end])) end++;
        if (end == 0 || end != inner.Length) return false;

        name = inner[..end].ToString().ToLowerInvariant();
        return true;
    }

    private static bool IsTagNameChar(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-';
}
