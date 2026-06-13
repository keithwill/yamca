using NUnit.Framework;
using Yamca.Agent.Board;

namespace Yamca.Agent.Tests.Board;

[TestFixture]
public class BoardPromptsTests
{
    private static BoardCard Card() =>
        new("0007", "Add OAuth login", null, "idea-id", "Plan the login flow.", Array.Empty<SubtaskItem>());

    private static BoardColumn Col(string id, int order, string name) =>
        new(id, order, name, null, Array.Empty<BoardCard>());

    [Test]
    public void BuildSeedPrompt_NullInstructions_DoesNotThrow()
    {
        var prompt = BoardPrompts.BuildSeedPrompt(Card(), Col("10-idea", 10, "idea"), instructions: null);

        Assert.That(prompt, Is.Not.Empty);
    }
}
