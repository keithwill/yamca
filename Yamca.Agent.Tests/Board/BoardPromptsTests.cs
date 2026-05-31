using NUnit.Framework;
using Yamca.Agent.Board;

namespace Yamca.Agent.Tests.Board;

[TestFixture]
public class BoardPromptsTests
{
    private static BoardCard Card() =>
        new("7", "Add OAuth login", null, "0007-add-oauth.md", "10-idea", "/x/0007-add-oauth.md", "Plan the login flow.", Array.Empty<SubtaskItem>());

    private static BoardColumn Col(string dir, int order, string name) =>
        new(dir, order, name, $"/x/{dir}", Array.Empty<BoardCard>());

    [Test]
    public void BuildSeedPrompt_InlinesStepCardAndInstructions()
    {
        var prompt = BoardPrompts.BuildSeedPrompt(Card(), Col("20-analyze", 20, "analyze"), "Investigate the codebase.");

        Assert.That(prompt, Does.Contain("\"analyze\" step"));
        Assert.That(prompt, Does.Contain("#7 \"Add OAuth login\""));
        Assert.That(prompt, Does.Contain("Plan the login flow."));
        Assert.That(prompt, Does.Contain("Investigate the codebase."));
        // The old system-message handoff and its generic completion blurb are gone.
        Assert.That(prompt, Does.Not.Contain("system context"));
        Assert.That(prompt, Does.Not.Contain("board_move_card"));
        Assert.That(prompt, Does.Not.Contain("tracked separately"));
    }

    [Test]
    public void BuildSeedPrompt_BlankInstructions_OmitsInstructionsSection()
    {
        var prompt = BoardPrompts.BuildSeedPrompt(Card(), Col("10-idea", 10, "idea"), instructions: null);

        Assert.That(prompt, Does.Contain("#7 \"Add OAuth login\""));
        Assert.That(prompt, Does.Not.Contain("step instructions"));
    }
}
