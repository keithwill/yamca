using NUnit.Framework;
using Yamca.Agent.Board;

namespace Yamca.Agent.Tests.Board;

[TestFixture]
public class CardMarkdownTests
{
    private static BoardCard Card(string body) =>
        new(7, "Add OAuth", "feat/oauth", "col", body, Array.Empty<TaskItem>(), CardPriority.High);

    [Test]
    public void Render_EmitsFrontmatterAndBody()
    {
        var md = CardMarkdown.Render(Card("Plan the flow."));

        Assert.That(md, Does.StartWith("---\n"));
        Assert.That(md, Does.Contain("id: 7"));
        Assert.That(md, Does.Contain("title: \"Add OAuth\""));
        Assert.That(md, Does.Contain("branch: feat/oauth"));
        Assert.That(md, Does.Contain("priority: high"));
        Assert.That(md, Does.Contain("Plan the flow."));
    }

    [Test]
    public void Render_DoesNotEmitChecklist_EvenWhenBodyContainsCheckboxText()
    {
        // Tasks are no longer rendered into the markdown; checkbox-looking text only appears if the
        // body itself contains it (it is plain prose to Render, not a task source).
        var md = CardMarkdown.Render(Card("intro\n- [ ] literal line in body"));
        Assert.That(md, Does.Contain("- [ ] literal line in body"), "body prose is emitted verbatim");
    }

    [Test]
    public void Render_OmitsBranch_WhenAbsent()
    {
        var card = new BoardCard(1, "T", null, "col", "body", Array.Empty<TaskItem>(), CardPriority.Normal);
        Assert.That(CardMarkdown.Render(card), Does.Not.Contain("branch:"));
    }

    [Test]
    public void Parse_SplitsFrontmatter_AndKeepsChecklistLinesInBodyVerbatim()
    {
        var parsed = CardMarkdown.Parse("---\ntitle: Foo\nbranch: feat/x\npriority: low\n---\nprose line\n- [ ] one\n- [x] two");

        Assert.That(parsed.Title, Is.EqualTo("Foo"));
        Assert.That(parsed.Branch, Is.EqualTo("feat/x"));
        Assert.That(parsed.Priority, Is.EqualTo(CardPriority.Low));
        // Checklist-looking lines are no longer pulled out — they stay in the body as prose.
        Assert.That(parsed.Body, Is.EqualTo("prose line\n- [ ] one\n- [x] two"));
    }

    [Test]
    public void Parse_MissingFrontmatter_LeavesFieldsNull()
    {
        var parsed = CardMarkdown.Parse("just a body\n- [ ] todo");

        Assert.That(parsed.Title, Is.Null);
        Assert.That(parsed.Branch, Is.Null);
        Assert.That(parsed.Priority, Is.Null);
        Assert.That(parsed.Body, Is.EqualTo("just a body\n- [ ] todo"));
    }

    [Test]
    public void RenderParse_RoundTrips()
    {
        var original = Card("Body text here.");
        var parsed = CardMarkdown.Parse(CardMarkdown.Render(original));

        Assert.That(parsed.Title, Is.EqualTo(original.Title));
        Assert.That(parsed.Branch, Is.EqualTo(original.Branch));
        Assert.That(parsed.Priority, Is.EqualTo(original.Priority));
        Assert.That(parsed.Body, Is.EqualTo(original.Body));
    }
}
