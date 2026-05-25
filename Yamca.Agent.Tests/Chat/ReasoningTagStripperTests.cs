using Yamca.Agent.Chat;

namespace Yamca.Agent.Tests.Chat;

[TestFixture]
public class ReasoningTagStripperTests
{
    [Test]
    public void NoTags_PassesThroughVerbatim()
    {
        var s = new ReasoningTagStripper();
        var r = s.Process("hello world");
        Assert.That(r.Visible, Is.EqualTo("hello world"));
        Assert.That(r.Reasoning, Is.Empty);
        Assert.That(r.JustClosed, Is.False);
    }

    [Test]
    public void SingleChunk_SplitsReasoningFromVisible()
    {
        var s = new ReasoningTagStripper();
        var r = s.Process("before<think>secret</think>after");
        Assert.That(r.Visible, Is.EqualTo("beforeafter"));
        Assert.That(r.Reasoning, Is.EqualTo("secret"));
        Assert.That(r.JustClosed, Is.True);
    }

    [Test]
    public void OpenTagSplitAcrossChunks_IsRecognized()
    {
        var s = new ReasoningTagStripper();
        var a = s.Process("hi <thi");
        var b = s.Process("nk>plan</think>done");

        Assert.That(a.Visible, Is.EqualTo("hi "));
        Assert.That(a.Reasoning, Is.Empty);
        Assert.That(b.Visible, Is.EqualTo("done"));
        Assert.That(b.Reasoning, Is.EqualTo("plan"));
        Assert.That(b.JustClosed, Is.True);
    }

    [Test]
    public void CloseTagSplitAcrossChunks_IsRecognized()
    {
        var s = new ReasoningTagStripper();
        var a = s.Process("<think>reason</thi");
        var b = s.Process("nk>visible");

        Assert.That(a.Reasoning, Is.EqualTo("reason"));
        Assert.That(a.JustClosed, Is.False);
        Assert.That(b.Reasoning, Is.Empty);
        Assert.That(b.Visible, Is.EqualTo("visible"));
        Assert.That(b.JustClosed, Is.True);
    }

    [Test]
    public void ReasoningStreamsAcrossMultipleChunks()
    {
        var s = new ReasoningTagStripper();
        var a = s.Process("<think>part one ");
        var b = s.Process("part two");
        var c = s.Process("</think>answer");

        Assert.That(a.Reasoning, Is.EqualTo("part one "));
        Assert.That(b.Reasoning, Is.EqualTo("part two"));
        Assert.That(c.Visible, Is.EqualTo("answer"));
        Assert.That(c.JustClosed, Is.True);
    }

    [Test]
    public void MultipleReasoningBlocks_InOneChunk()
    {
        var s = new ReasoningTagStripper();
        var r = s.Process("a<think>x</think>b<think>y</think>c");
        Assert.That(r.Visible, Is.EqualTo("abc"));
        Assert.That(r.Reasoning, Is.EqualTo("xy"));
        Assert.That(r.JustClosed, Is.True);
    }

    [Test]
    public void CaseInsensitive()
    {
        var s = new ReasoningTagStripper();
        var r = s.Process("<Think>plan</THINK>ok");
        Assert.That(r.Reasoning, Is.EqualTo("plan"));
        Assert.That(r.Visible, Is.EqualTo("ok"));
    }

    [Test]
    public void OpenTagWithAttributes()
    {
        var s = new ReasoningTagStripper();
        var r = s.Process("<think id=\"1\">plan</think>ok");
        Assert.That(r.Reasoning, Is.EqualTo("plan"));
        Assert.That(r.Visible, Is.EqualTo("ok"));
    }

    [Test]
    public void UnknownTags_PassedThroughAsVisible()
    {
        var s = new ReasoningTagStripper();
        var r = s.Process("hello <b>bold</b> world");
        Assert.That(r.Visible, Is.EqualTo("hello <b>bold</b> world"));
        Assert.That(r.Reasoning, Is.Empty);
    }

    [Test]
    public void LessThanInNormalText_DoesNotStall()
    {
        var s = new ReasoningTagStripper();
        var r1 = s.Process("if a < b then ");
        var r2 = s.Process("c > d");
        Assert.That(r1.Visible + r2.Visible, Is.EqualTo("if a < b then c > d"));
    }

    [Test]
    public void NeverClosedTag_FlushReturnsBufferedReasoning()
    {
        var s = new ReasoningTagStripper();
        var r1 = s.Process("<think>still thinking");
        var r2 = s.Flush();
        Assert.That(r1.Reasoning, Is.EqualTo("still thinking"));
        Assert.That(r2.Reasoning + r2.Visible, Is.Empty); // nothing left buffered
    }

    [Test]
    public void NeverClosedTag_PartialClosePending_FlushedAsReasoning()
    {
        var s = new ReasoningTagStripper();
        var r1 = s.Process("<think>plan</thi");
        var r2 = s.Flush();
        Assert.That(r1.Reasoning, Is.EqualTo("plan"));
        Assert.That(r2.Reasoning, Is.EqualTo("</thi"));
    }

    [Test]
    public void CustomTagSet_RecognizesReasoning()
    {
        var s = new ReasoningTagStripper(new[] { "reasoning" });
        var r = s.Process("<reasoning>x</reasoning>y");
        Assert.That(r.Reasoning, Is.EqualTo("x"));
        Assert.That(r.Visible, Is.EqualTo("y"));

        // <think> not in the set — treated as visible
        var s2 = new ReasoningTagStripper(new[] { "reasoning" });
        var r2 = s2.Process("<think>x</think>y");
        Assert.That(r2.Visible, Is.EqualTo("<think>x</think>y"));
        Assert.That(r2.Reasoning, Is.Empty);
    }

    [Test]
    public void StrayLtGtInsideReasoning_PassThroughAsReasoning()
    {
        var s = new ReasoningTagStripper();
        var r = s.Process("<think>a <b> c</think>done");
        Assert.That(r.Reasoning, Is.EqualTo("a <b> c"));
        Assert.That(r.Visible, Is.EqualTo("done"));
    }

    [Test]
    public void EmptyDelta_NoCarry_ReturnsEmptyResult()
    {
        var s = new ReasoningTagStripper();
        var r = s.Process(string.Empty);
        Assert.That(r.Visible, Is.Empty);
        Assert.That(r.Reasoning, Is.Empty);
    }
}
