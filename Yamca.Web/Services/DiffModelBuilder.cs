using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace Yamca.Web.Services;

/// <summary>Render layout for <c>DiffView</c>: a single +/- column or two side-by-side columns.</summary>
public enum DiffViewMode { Unified, SideBySide }

/// <summary>How a single side (old or new) of a diff row relates to the other side.
/// <see cref="Empty"/> is the alignment filler shown opposite an insertion/deletion.</summary>
public enum DiffCellKind { Empty, Unchanged, Inserted, Deleted, Modified }

/// <summary>A run of text within a line. <see cref="Changed"/> marks the portion that differs,
/// used for word-level highlighting inside a modified line.</summary>
public sealed record DiffSegment(string Text, bool Changed);

/// <summary>One side of a diff row: its line number (null for filler), how it changed, and the
/// line broken into highlight segments.</summary>
public sealed record DiffCell(int? LineNumber, DiffCellKind Kind, IReadOnlyList<DiffSegment> Segments)
{
    public static readonly DiffCell Empty = new(null, DiffCellKind.Empty, Array.Empty<DiffSegment>());
    public bool IsChange => Kind is DiffCellKind.Inserted or DiffCellKind.Deleted or DiffCellKind.Modified;
}

/// <summary>An aligned old/new row. The unified view derives +/- lines from the two cells;
/// the side-by-side view renders them as two columns.</summary>
public sealed record DiffRow(DiffCell Old, DiffCell New)
{
    public bool IsUnchanged => Old.Kind == DiffCellKind.Unchanged && New.Kind == DiffCellKind.Unchanged;
}

/// <summary>A contiguous run of rows. <see cref="Collapsible"/> blocks are pure-unchanged runs the
/// UI may fold down to a few lines of context.</summary>
public sealed record DiffBlock(bool Collapsible, IReadOnlyList<DiffRow> Rows);

/// <summary>A rendered diff: blocks of rows plus summary counts. <see cref="TooLarge"/> is set when
/// the inputs exceed the render guard rails, so the UI can show a notice instead of a huge diff.</summary>
public sealed record DiffDocument(IReadOnlyList<DiffBlock> Blocks, int Insertions, int Deletions, bool TooLarge)
{
    public static readonly DiffDocument Empty = new(Array.Empty<DiffBlock>(), 0, 0, false);
    public bool IsEmpty => Insertions == 0 && Deletions == 0 && !TooLarge;
}

/// <summary>Turns a before/after text pair into a render-ready <see cref="DiffDocument"/> using
/// DiffPlex. Pure and stateless: callers (the chat tool-call card, approval prompt, and later the
/// card Changes tab) all funnel through here so there is a single diff representation to render.</summary>
public static class DiffModelBuilder
{
    private static readonly SideBySideDiffBuilder Builder = new(new Differ());

    // Guard rails: a pathological tool argument (e.g. a multi-megabyte generated file) must not lock
    // up the renderer. Past these limits we report TooLarge and let the UI fall back to a notice.
    private const int MaxLines = 4000;
    private const int MaxChars = 400_000;

    public static DiffDocument Build(string? oldText, string? newText)
    {
        oldText ??= "";
        newText ??= "";
        if (oldText.Length + newText.Length > MaxChars)
            return new DiffDocument(Array.Empty<DiffBlock>(), 0, 0, TooLarge: true);

        var model = Builder.BuildDiffModel(oldText, newText, ignoreWhitespace: false);
        var old = model.OldText.Lines;
        var neu = model.NewText.Lines;
        var count = Math.Max(old.Count, neu.Count);
        if (count > MaxLines)
            return new DiffDocument(Array.Empty<DiffBlock>(), 0, 0, TooLarge: true);

        var rows = new List<DiffRow>(count);
        int insertions = 0, deletions = 0;
        for (var i = 0; i < count; i++)
        {
            var oldCell = ToCell(i < old.Count ? old[i] : null);
            var newCell = ToCell(i < neu.Count ? neu[i] : null);
            if (oldCell.Kind is DiffCellKind.Deleted or DiffCellKind.Modified) deletions++;
            if (newCell.Kind is DiffCellKind.Inserted or DiffCellKind.Modified) insertions++;
            rows.Add(new DiffRow(oldCell, newCell));
        }

        return new DiffDocument(GroupBlocks(rows), insertions, deletions, TooLarge: false);
    }

    private static DiffCell ToCell(DiffPiece? piece)
    {
        if (piece is null || piece.Type == ChangeType.Imaginary)
            return DiffCell.Empty;

        var kind = piece.Type switch
        {
            ChangeType.Inserted => DiffCellKind.Inserted,
            ChangeType.Deleted => DiffCellKind.Deleted,
            ChangeType.Modified => DiffCellKind.Modified,
            _ => DiffCellKind.Unchanged,
        };

        // Word-level highlight only where a line was modified — fully added/removed lines are already
        // conveyed by the row colour, so highlighting their whole text would just add noise.
        IReadOnlyList<DiffSegment> segments;
        if (piece.Type == ChangeType.Modified && piece.SubPieces.Count > 0)
        {
            var segs = new List<DiffSegment>(piece.SubPieces.Count);
            foreach (var sub in piece.SubPieces)
            {
                if (sub.Type == ChangeType.Imaginary) continue;
                var changed = sub.Type is ChangeType.Inserted or ChangeType.Deleted or ChangeType.Modified;
                segs.Add(new DiffSegment(sub.Text ?? "", changed));
            }
            segments = segs;
        }
        else
        {
            segments = new[] { new DiffSegment(piece.Text ?? "", false) };
        }

        return new DiffCell(piece.Position, kind, segments);
    }

    // Group consecutive rows by changed-vs-unchanged so the UI can fold long unchanged runs.
    private static IReadOnlyList<DiffBlock> GroupBlocks(List<DiffRow> rows)
    {
        var blocks = new List<DiffBlock>();
        var i = 0;
        while (i < rows.Count)
        {
            var unchanged = rows[i].IsUnchanged;
            var j = i;
            while (j < rows.Count && rows[j].IsUnchanged == unchanged) j++;
            blocks.Add(new DiffBlock(unchanged, rows.GetRange(i, j - i)));
            i = j;
        }
        return blocks;
    }
}
