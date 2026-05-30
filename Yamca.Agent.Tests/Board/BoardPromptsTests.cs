using NUnit.Framework;
using Yamca.Agent.Board;

namespace Yamca.Agent.Tests.Board;

[TestFixture]
public class BoardPromptsTests
{
    private static BoardCard Card() =>
        new("7", "Add OAuth login", null, "0007-add-oauth.md", "10-idea", "/x/0007-add-oauth.md", "body", Array.Empty<SubtaskItem>());

    private static BoardColumn Col(string dir, int order, string name) =>
        new(dir, order, name, $"/x/{dir}", Array.Empty<BoardCard>());

    [Test]
    public void BuildSeedPrompt_NamesStepCardAndNextColumn()
    {
        var prompt = BoardPrompts.BuildSeedPrompt(Card(), Col("10-idea", 10, "idea"), Col("20-analyze", 20, "analyze"));

        Assert.That(prompt, Does.Contain("\"idea\" step"));
        Assert.That(prompt, Does.Contain("#7 \"Add OAuth login\""));
        Assert.That(prompt, Does.Contain("board_move_card"));
        Assert.That(prompt, Does.Contain("\"analyze\""));
        Assert.That(prompt, Does.Contain("tracked separately"));
    }

    [Test]
    public void BuildSeedPrompt_FinalColumn_HasNoMoveTarget()
    {
        var prompt = BoardPrompts.BuildSeedPrompt(Card(), Col("50-done", 50, "done"), next: null);

        Assert.That(prompt, Does.Contain("final column"));
        Assert.That(prompt, Does.Not.Contain("board_move_card"));
    }

    [Test]
    public void BuildStepInstruction_PrefixesColumnHeader()
    {
        var instr = BoardPrompts.BuildStepInstruction(Col("30-implement", 30, "implement"), "Write the code.");
        Assert.That(instr, Does.Contain("# Board step: implement"));
        Assert.That(instr, Does.Contain("Write the code."));
    }
}
