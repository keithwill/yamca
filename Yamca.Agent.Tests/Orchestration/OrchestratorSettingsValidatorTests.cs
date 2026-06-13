using NUnit.Framework;
using Yamca.Agent.Board;
using Yamca.Agent.Orchestration;
using Yamca.Agent.Settings;

namespace Yamca.Agent.Tests.Orchestration;

[TestFixture]
public class OrchestratorSettingsValidatorTests
{
    // analyze is a work column (has instructions); done is resting (none).
    private static readonly BoardSnapshot Board = new(new[]
    {
        new BoardColumn("analyze-id", 20, "analyze", "Analyze the card.", Array.Empty<BoardCard>()),
        new BoardColumn("done-id", 50, "done", null, Array.Empty<BoardCard>()),
    });

    private static readonly EndpointsSettings Endpoints = new(
        new[] { new EndpointSettings(Guid.NewGuid(), "local", "http://localhost:8080/v1", "", "model") },
        DefaultId: Guid.Empty);

    private static OrchestratorSettings Valid => OrchestratorSettings.Default with
    {
        EnabledColumns = new[] { "analyze-id" },
    };

    [Test]
    public void ValidConfig_NoErrors()
    {
        var result = OrchestratorSettingsValidator.Validate(Valid, Endpoints, Board);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Warnings, Is.Empty);
    }

    [Test]
    public void NoEnabledColumns_Error()
    {
        var result = OrchestratorSettingsValidator.Validate(
            Valid with { EnabledColumns = Array.Empty<string>() }, Endpoints, Board);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Contains("No board columns"));
    }

    [Test]
    public void UnknownColumn_Error()
    {
        var result = OrchestratorSettingsValidator.Validate(
            Valid with { EnabledColumns = new[] { "ghost-id" } }, Endpoints, Board);

        Assert.That(result.Errors, Has.Some.Contains("no longer exists"));
    }

    [Test]
    public void RestingColumn_Error()
    {
        var result = OrchestratorSettingsValidator.Validate(
            Valid with { EnabledColumns = new[] { "done-id" } }, Endpoints, Board);

        Assert.That(result.Errors, Has.Some.Contains("no step instructions"));
    }

    [Test]
    public void NoEndpoints_Error()
    {
        var empty = new EndpointsSettings(Array.Empty<EndpointSettings>(), Guid.Empty);

        var result = OrchestratorSettingsValidator.Validate(Valid, empty, Board);

        Assert.That(result.Errors, Has.Some.Contains("No endpoints"));
    }

    [Test]
    public void StaleEndpointId_Error()
    {
        var result = OrchestratorSettingsValidator.Validate(
            Valid with { EndpointId = Guid.NewGuid() }, Endpoints, Board);

        Assert.That(result.Errors, Has.Some.Contains("endpoint"));
    }

    [Test]
    public void EmptyToolList_Error()
    {
        var result = OrchestratorSettingsValidator.Validate(
            Valid with { AllowedTools = Array.Empty<string>() }, Endpoints, Board);

        Assert.That(result.Errors, Has.Some.Contains("allowed-tools"));
    }

    [Test]
    public void MissingBoardMoveCard_WarnsButValid()
    {
        var result = OrchestratorSettingsValidator.Validate(
            Valid with { AllowedTools = new[] { "read_file" } }, Endpoints, Board);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Warnings, Has.Some.Contains("board_move_card"));
    }
}
