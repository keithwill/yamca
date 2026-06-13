using NUnit.Framework;
using Yamca.Agent.Board;

namespace Yamca.Agent.Tests.Board;

[TestFixture]
public class CardMarkdownTests
{
    private static BoardCard Card(string body, params SubtaskItem[] subtasks) =>
        new("0007", "Add OAuth", "feat/oauth", "col", body, subtasks, CardPriority.High);

    [Test]
    public void Render_EmitsFrontmatterBodyAndChecklist()
    {
        var md = CardMarkdown.Render(Card("Plan the flow.", new SubtaskItem("a", false), new SubtaskItem("b", true)));

        Assert.That(md, Does.StartWith("---\n"));
        Assert.That(md, Does.Contain("id: 0007"));
        Assert.That(md, Does.Contain("title: \"Add OAuth\""));
        Assert.That(md, Does.Contain("branch: feat/oauth"));
        Assert.That(md, Does.Contain("priority: high"));
        Assert.That(md, Does.Contain("Plan the flow."));
        Assert.That(md, Does.Contain("- [ ] a"));
        Assert.That(md, Does.Contain("- [x] b"));
    }

    [Test]
    public void Render_OmitsBranch_WhenAbsent()
    {
        var card = new BoardCard("0001", "T", null, "col", "body", Array.Empty<SubtaskItem>(), CardPriority.Normal);
        Assert.That(CardMarkdown.Render(card), Does.Not.Contain("branch:"));
    }

    [Test]
    public void Parse_SplitsFrontmatterBodyAndChecklist()
    {
        var parsed = CardMarkdown.Parse("---\ntitle: Foo\nbranch: feat/x\npriority: low\n---\nprose line\n- [ ] one\n- [x] two");

        Assert.That(parsed.Title, Is.EqualTo("Foo"));
        Assert.That(parsed.Branch, Is.EqualTo("feat/x"));
        Assert.That(parsed.Priority, Is.EqualTo(CardPriority.Low));
        Assert.That(parsed.Body, Is.EqualTo("prose line"));
        Assert.That(parsed.Subtasks, Is.EqualTo(new[] { new SubtaskItem("one", false), new SubtaskItem("two", true) }));
    }

    [Test]
    public void Parse_MissingFrontmatter_LeavesFieldsNull()
    {
        var parsed = CardMarkdown.Parse("just a body\n- [ ] todo");

        Assert.That(parsed.Title, Is.Null);
        Assert.That(parsed.Branch, Is.Null);
        Assert.That(parsed.Priority, Is.Null);
        Assert.That(parsed.Body, Is.EqualTo("just a body"));
        Assert.That(parsed.Subtasks.Single(), Is.EqualTo(new SubtaskItem("todo", false)));
    }

    [Test]
    public void RenderParse_RoundTrips()
    {
        var original = Card("Body text here.", new SubtaskItem("step", false));
        var parsed = CardMarkdown.Parse(CardMarkdown.Render(original));

        Assert.That(parsed.Title, Is.EqualTo(original.Title));
        Assert.That(parsed.Branch, Is.EqualTo(original.Branch));
        Assert.That(parsed.Priority, Is.EqualTo(original.Priority));
        Assert.That(parsed.Body, Is.EqualTo(original.Body));
        Assert.That(parsed.Subtasks, Is.EqualTo(original.Subtasks));
    }
}
