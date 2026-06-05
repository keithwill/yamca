using System.Text.Json;
using Yamca.Agent.Chat;
using Yamca.Agent.Settings;
using Yamca.Agent.Subagents;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;

namespace Yamca.Agent.Tests.Tools;

[TestFixture]
public class SubagentRunToolTests
{
    private InMemorySessionSettings _settings = null!;
    private SubagentRunTool _tool = null!;

    [SetUp]
    public void SetUp()
    {
        _settings = new InMemorySessionSettings();
        _tool = new SubagentRunTool(new NoopRunner(), _settings);
    }

    private void Define(params SubagentDefinition[] agents) =>
        _settings.UserSubagents = new SubagentRegistry(agents);

    private static SubagentDefinition Agent(string name, string description) =>
        new(Guid.NewGuid(), name, description, "Do the task.", Array.Empty<string>());

    [Test]
    public void NotExposed_WhenNoSubagentsConfigured()
    {
        Assert.That(_tool.ExposedToLlm, Is.False);
    }

    [Test]
    public void Exposed_WhenAtLeastOneSubagentConfigured()
    {
        Define(Agent("explorer", "Searches the codebase."));
        Assert.That(_tool.ExposedToLlm, Is.True);
    }

    [Test]
    public void Schema_EnumeratesAgentNames_AndDescribesEach()
    {
        Define(Agent("explorer", "Searches the codebase."),
               Agent("reviewer", "Reviews a diff."));

        using var doc = JsonDocument.Parse(_tool.ParametersSchema);
        var agentProp = doc.RootElement.GetProperty("properties").GetProperty("agent");

        var names = agentProp.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.That(names, Is.EqualTo(new[] { "explorer", "reviewer" }));

        var desc = agentProp.GetProperty("description").GetString();
        Assert.That(desc, Does.Contain("explorer: Searches the codebase."));
        Assert.That(desc, Does.Contain("reviewer: Reviews a diff."));
    }

    [Test]
    public void Schema_OmitsEnum_WhenNoSubagentsConfigured()
    {
        using var doc = JsonDocument.Parse(_tool.ParametersSchema);
        var agentProp = doc.RootElement.GetProperty("properties").GetProperty("agent");
        Assert.That(agentProp.TryGetProperty("enum", out _), Is.False);
    }

    [Test]
    public void Schema_EscapesSpecialCharactersInDescriptions()
    {
        Define(Agent("quoter", "Handles \"quotes\" and\nnewlines."));

        // Must round-trip as valid JSON despite the embedded quotes/newlines.
        using var doc = JsonDocument.Parse(_tool.ParametersSchema);
        var desc = doc.RootElement.GetProperty("properties").GetProperty("agent")
            .GetProperty("description").GetString();
        Assert.That(desc, Does.Contain("Handles \"quotes\" and\nnewlines."));
    }

    [Test]
    public void Description_ListsAvailableAgentNames()
    {
        Define(Agent("explorer", "Searches the codebase."),
               Agent("reviewer", "Reviews a diff."));

        Assert.That(_tool.Description, Does.Contain("explorer"));
        Assert.That(_tool.Description, Does.Contain("reviewer"));
    }

    private sealed class NoopRunner : ISubagentRunner
    {
        public void Bind(IChatCompletionClient parentClient) { }

        public Task<ToolResult> RunAsync(string agentName, string prompt, ToolContext parentContext, CancellationToken cancellationToken) =>
            Task.FromResult(ToolResult.Ok(""));

        public Task<SubagentOutcome> RunCoreAsync(string agentName, string prompt, ToolContext parentContext, string? loopRunId, CancellationToken cancellationToken) =>
            Task.FromResult(new SubagentOutcome(true, SubagentStatus.Success, "", false, null));
    }
}
