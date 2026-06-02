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
    public void BuildSeedPrompt_NullInstructions_DoesNotThrow()
    {
        var prompt = BoardPrompts.BuildSeedPrompt(Card(), Col("10-idea", 10, "idea"), instructions: null);

        Assert.That(prompt, Is.Not.Empty);
    }
}
