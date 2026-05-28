using System.Text;
using TreeSitter;

namespace Yamca.Agent.Tools.CodeIntel;

/// <summary>
/// Shared helpers for turning syntax-tree nodes into compact one-line signatures.
/// Per-language extractors decide *which* slice to pass; this collapses whitespace
/// and caps length so the rendered tree stays token-dense.
/// </summary>
internal static class SignatureFormatter
{
    public const int MaxSignatureLength = 200;

    /// <summary>
    /// Slice <paramref name="source"/> from <paramref name="declaration"/>'s start to
    /// <paramref name="body"/>'s start (or the declaration's end if no body), then
    /// collapse runs of whitespace to single spaces and cap to <see cref="MaxSignatureLength"/>.
    /// </summary>
    public static string SliceHeader(Node declaration, Node? body, string source)
    {
        int endIndex = body is not null ? body.StartIndex : declaration.EndIndex;
        int startIndex = declaration.StartIndex;
        if (endIndex < startIndex || startIndex < 0 || endIndex > source.Length)
            return Collapse(declaration.Text ?? string.Empty);
        return Collapse(source.AsSpan(startIndex, endIndex - startIndex));
    }

    public static string Collapse(string text) => Collapse(text.AsSpan());

    public static string Collapse(ReadOnlySpan<char> text)
    {
        var sb = new StringBuilder(Math.Min(text.Length, MaxSignatureLength));
        var lastWasSpace = false;
        foreach (var c in text)
        {
            if (c == '\r' || c == '\n' || c == '\t' || c == ' ')
            {
                if (lastWasSpace) continue;
                if (sb.Length == 0) continue;
                sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }

        // Trim trailing space and any trailing delimiter (`{`, `=>`, `:`) so the
        // signature reads cleanly without dangling body openers.
        while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        while (sb.Length > 0 && (sb[^1] == '{' || sb[^1] == ':')) { sb.Length--; while (sb.Length > 0 && sb[^1] == ' ') sb.Length--; }

        if (sb.Length > MaxSignatureLength)
        {
            sb.Length = MaxSignatureLength;
            sb.Append('…');
        }

        return sb.ToString();
    }
}
