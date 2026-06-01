using NUnit.Framework;
using Yamca.Web.Services;

namespace Yamca.Web.Tests;

[TestFixture]
public class DiffModelBuilderTests
{
    [Test]
    public void IdenticalText_IsEmpty()
    {
        var doc = DiffModelBuilder.Build("a\nb\nc\n", "a\nb\nc\n");
        Assert.That(doc.IsEmpty, Is.True);
        Assert.That(doc.Insertions, Is.EqualTo(0));
        Assert.That(doc.Deletions, Is.EqualTo(0));
    }

    [Test]
    public void LineEndingOnlyDifference_IsNotADiff()
    {
        // The Changes tab feeds git output (re-joined with the OS newline, CRLF on Windows) against
        // the working copy read from disk (often LF). Those must not register as an all-modified file.
        var doc = DiffModelBuilder.Build("a\nb\nc\n", "a\r\nb\r\nc\r\n");
        Assert.That(doc.IsEmpty, Is.True);
    }

    [Test]
    public void PureAddition_CountsInsertionsOnly()
    {
        var doc = DiffModelBuilder.Build("", "line1\nline2\n");
        Assert.That(doc.Deletions, Is.EqualTo(0));
        Assert.That(doc.Insertions, Is.GreaterThanOrEqualTo(2));
        Assert.That(doc.IsEmpty, Is.False);
    }

    [Test]
    public void ModifiedLine_CountsBothSides_AndHasWordLevelHighlight()
    {
        var doc = DiffModelBuilder.Build("the quick brown fox\n", "the quick red fox\n");

        Assert.That(doc.Insertions, Is.EqualTo(1));
        Assert.That(doc.Deletions, Is.EqualTo(1));

        // The single modified row should carry sub-line segments, at least one of which is the
        // changed word — that's what drives intra-line highlighting.
        var rows = doc.Blocks.SelectMany(bk => bk.Rows).ToList();
        var modifiedNew = rows.Select(r => r.New).First(c => c.Kind == DiffCellKind.Modified);
        Assert.That(modifiedNew.Segments.Count, Is.GreaterThan(1));
        Assert.That(modifiedNew.Segments.Any(s => s.Changed), Is.True);
        Assert.That(string.Concat(modifiedNew.Segments.Select(s => s.Text)), Is.EqualTo("the quick red fox"));
    }

    [Test]
    public void LongUnchangedRun_FormsCollapsibleBlock()
    {
        var unchanged = string.Concat(Enumerable.Range(0, 30).Select(i => $"line{i}\n"));
        var doc = DiffModelBuilder.Build(unchanged, unchanged + "added\n");

        // The leading 30 identical lines are one collapsible block; the trailing addition is not.
        Assert.That(doc.Blocks.Any(bk => bk.Collapsible && bk.Rows.Count >= 30), Is.True);
        Assert.That(doc.Blocks.Any(bk => !bk.Collapsible), Is.True);
    }

    [Test]
    public void HugeInput_ReportsTooLarge()
    {
        var big = new string('x', 250_000);
        var doc = DiffModelBuilder.Build(big, big + "y");
        Assert.That(doc.TooLarge, Is.True);
        Assert.That(doc.IsEmpty, Is.False); // TooLarge is not "empty"
    }
}

[TestFixture]
public class FileChangeArgsTests
{
    [Test]
    public void EditFile_ParsesOldAndNew()
    {
        var json = """{"path":"a.cs","old_string":"foo","new_string":"bar"}""";
        var change = FileChangeArgs.Parse("edit_file", json);

        Assert.That(change, Is.Not.Null);
        Assert.That(change!.Path, Is.EqualTo("a.cs"));
        Assert.That(change.OldText, Is.EqualTo("foo"));
        Assert.That(change.NewText, Is.EqualTo("bar"));
    }

    [Test]
    public void WriteFile_UsesEmptyOldSide()
    {
        var json = """{"path":"a.cs","content":"hello"}""";
        var change = FileChangeArgs.Parse("write_file", json);

        Assert.That(change, Is.Not.Null);
        Assert.That(change!.OldText, Is.EqualTo(""));
        Assert.That(change.NewText, Is.EqualTo("hello"));
    }

    [Test]
    public void NonDiffTool_ReturnsNull()
    {
        Assert.That(FileChangeArgs.Parse("read_file", """{"path":"a.cs"}"""), Is.Null);
    }

    [Test]
    public void PartialStreamingJson_ReturnsNull()
    {
        // Arguments stream in token-by-token; an incomplete object must not throw.
        Assert.That(FileChangeArgs.Parse("edit_file", """{"path":"a.cs","old_str"""), Is.Null);
    }

    [Test]
    public void EditFile_MissingNewString_ReturnsNull()
    {
        Assert.That(FileChangeArgs.Parse("edit_file", """{"path":"a.cs","old_string":"foo"}"""), Is.Null);
    }
}
