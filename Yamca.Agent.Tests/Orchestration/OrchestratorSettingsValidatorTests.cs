using NUnit.Framework;
using Yamca.Agent.Board;
using Yamca.Agent.Orchestration;
using Yamca.Agent.Settings;

namespace Yamca.Agent.Tests.Orchestration;

[TestFixture]
public class OrchestratorSettingsValidatorTests
{
    private static readonly BoardSnapshot Board = new(new[]
    {
        new BoardColumn("20-analyze", 20, "analyze", @"C:\board\20-analyze", Array.Empty<BoardCard>()),
        new BoardColumn("50-done", 50, "done", @"C:\board\50-done", Array.Empty<BoardCard>()),
    });

    private static readonly EndpointsSettings Endpoints = new(
        new[] { new EndpointSettings(Guid.NewGuid(), "local", "http://localhost:8080/v1", "", "model") },
        DefaultId: Guid.Empty);

    // 20-analyze is a work column; 50-done is resting.
    private static bool HasInstructions(string dir) =>
        string.Equals(dir, "20-analyze", StringComparison.OrdinalIgnoreCase);

    private static OrchestratorSettings Valid => OrchestratorSettings.Default with
    {
        EnabledColumns = new[] { "20-analyze" },
    };

    [Test]
    public void ValidConfig_NoErrors()
    {
        var result = OrchestratorSettingsValidator.Validate(Valid, Endpoints, Board, HasInstructions);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Warnings, Is.Empty);
    }

    [Test]
    public void NoEnabledColumns_Error()
    {
        var result = OrchestratorSettingsValidator.Validate(
            Valid with { EnabledColumns = Array.Empty<string>() }, Endpoints, Board, HasInstructions);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Contains("No board columns"));
    }

    [Test]
    public void UnknownColumn_Error()
    {
        var result = OrchestratorSettingsValidator.Validate(
            Valid with { EnabledColumns = new[] { "99-ghost" } }, Endpoints, Board, HasInstructions);

        Assert.That(result.Errors, Has.Some.Contains("99-ghost"));
    }

    [Test]
    public void RestingColumn_Error()
    {
        var result = OrchestratorSettingsValidator.Validate(
            Valid with { EnabledColumns = new[] { "50-done" } }, Endpoints, Board, HasInstructions);

        Assert.That(result.Errors, Has.Some.Contains("no step instructions"));
    }

    [Test]
    public void NoEndpoints_Error()
    {
        var empty = new EndpointsSettings(Array.Empty<EndpointSettings>(), Guid.Empty);

        var result = OrchestratorSettingsValidator.Validate(Valid, empty, Board, HasInstructions);

        Assert.That(result.Errors, Has.Some.Contains("No endpoints"));
    }

    [Test]
    public void StaleEndpointId_Error()
    {
        var result = OrchestratorSettingsValidator.Validate(
            Valid with { EndpointId = Guid.NewGuid() }, Endpoints, Board, HasInstructions);

        Assert.That(result.Errors, Has.Some.Contains("endpoint"));
    }

    [Test]
    public void EmptyToolList_Error()
    {
        var result = OrchestratorSettingsValidator.Validate(
            Valid with { AllowedTools = Array.Empty<string>() }, Endpoints, Board, HasInstructions);

        Assert.That(result.Errors, Has.Some.Contains("allowed-tools"));
    }

    [Test]
    public void MissingBoardMoveCard_WarnsButValid()
    {
        var result = OrchestratorSettingsValidator.Validate(
            Valid with { AllowedTools = new[] { "read_file" } }, Endpoints, Board, HasInstructions);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Warnings, Has.Some.Contains("board_move_card"));
    }
}
