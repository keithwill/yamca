using NUnit.Framework;
using Yamca.Agent.Board;
using Yamca.Agent.Chat;
using Yamca.Agent.Orchestration;
using Yamca.Agent.Settings;
using Yamca.Agent.Storage;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;
using WorkspaceImpl = Yamca.Agent.Workspace.Workspace;

namespace Yamca.Agent.Tests.Orchestration;

[TestFixture]
public class OrchestratorCardRunnerTests
{
    private string _root = null!;
    private WorkspaceImpl _workspace = null!;
    private BoardStore _boardStore = null!;
    private OrchestratorCardRunner _runner = null!;
    private string _implementId = null!;
    private BoardCard _card = null!;
    private BoardColumn _column = null!;

    [SetUp]
    public async Task SetUp()
    {
        _root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "yamca-tests", "ocr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _workspace = new WorkspaceImpl(_root, _root);
        _boardStore = new BoardStore(new YamcaStore(filePath: null));
        _runner = new OrchestratorCardRunner(_boardStore);

        var seeded = await _boardStore.ReadAsync(CancellationToken.None);
        var analyzeId = seeded.FindColumn("analyze")!.Id;
        _implementId = seeded.FindColumn("implement")!.Id;
        await _boardStore.AddCardAsync(analyzeId, "Test card", "Do the thing.", null, CardPriority.Normal, CancellationToken.None);

        var snapshot = await _boardStore.ReadAsync(CancellationToken.None);
        _column = snapshot.FindColumn("analyze")!;
        _card = snapshot.FindCard(1)!;
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private OrchestratorRunRequest Request(
        IChatCompletionClient client,
        IReadOnlyList<ITool> tools,
        OrchestratorSettings? settings = null) =>
        new(
            _card, _column, "Analyze the card.",
            _workspace, client, tools,
            settings ?? OrchestratorSettings.Default with
            {
                AllowedTools = tools.Select(t => t.Name).ToList(),
                MaxTurnsPerRun = 3,
            },
            SessionMaxToolIterations: 10,
            RunId: "run1",
            Observer: NoopOrchestratorObserver.Instance);

    // A board_move_card stand-in that really moves the card in the store, so the runner's
    // authoritative board re-read sees the change.
    private StubTool MoveCardTool() => new("board_move_card", responder: (_, _) =>
    {
        _boardStore.MoveCardAsync(_card.Id, _implementId, CancellationToken.None).GetAwaiter().GetResult();
        return ToolResult.Ok("Moved card 0001 to implement.");
    });

    [Test]
    public async Task MoveViaTool_SucceedsOnFastPath()
    {
        var client = new FakeChatCompletionClient()
            .EnqueueToolCall("c1", "board_move_card", """{"card":"0001","to_column":"implement"}""");
        var tools = new ITool[] { MoveCardTool() };

        var result = await _runner.RunAsync(Request(client, tools), CancellationToken.None);

        Assert.That(result.Outcome.Succeeded, Is.True);
        Assert.That(result.TurnCount, Is.EqualTo(1));
    }

    [Test]
    public async Task MoveBySideEffect_DetectedByBoardReread()
    {
        // The card moves via a tool that is NOT board_move_card; success must still be
        // detected by the post-turn board re-read.
        var sneaky = new StubTool("do_work", responder: (_, _) =>
        {
            _boardStore.MoveCardAsync(_card.Id, _implementId, CancellationToken.None).GetAwaiter().GetResult();
            return ToolResult.Ok("worked");
        });
        var client = new FakeChatCompletionClient()
            .EnqueueToolCall("c1", "do_work", "{}")
            .EnqueueText("All done.");

        var result = await _runner.RunAsync(Request(client, new ITool[] { sneaky }), CancellationToken.None);

        Assert.That(result.Outcome.Succeeded, Is.True);
        Assert.That(result.TurnCount, Is.EqualTo(1));
    }

    [Test]
    public async Task NoMove_IssuesContinuationPrompt_ThenFailsWhenTurnsExhausted()
    {
        var client = new FakeChatCompletionClient()
            .EnqueueText("I looked around.")
            .EnqueueText("Still thinking about it.");
        var tools = new ITool[] { new StubTool("read_file") };
        var settings = OrchestratorSettings.Default with
        {
            AllowedTools = new[] { "read_file" },
            MaxTurnsPerRun = 2,
        };

        var result = await _runner.RunAsync(Request(client, tools, settings), CancellationToken.None);

        Assert.That(result.Outcome.Succeeded, Is.False);
        Assert.That(result.Outcome.Retryable, Is.True);
        Assert.That(result.Outcome.FailureReason, Does.Contain("within 2 turns"));
        Assert.That(result.Outcome.FailureReason, Does.Contain("Still thinking about it."));
        Assert.That(result.TurnCount, Is.EqualTo(2));

        // The second turn must have been seeded with the continuation prompt.
        Assert.That(client.Calls, Has.Count.EqualTo(2));
        Assert.That(client.Calls[1].Messages.Last(m => m.Role == ChatRole.User).Content,
            Is.EqualTo(OrchestratorPrompts.ContinuationPrompt));
    }

    [Test]
    public async Task SeedPrompt_InlinesCardAndInstructions()
    {
        var client = new FakeChatCompletionClient().EnqueueText("ok");
        var tools = new ITool[] { new StubTool("read_file") };
        var settings = OrchestratorSettings.Default with
        {
            AllowedTools = new[] { "read_file" },
            MaxTurnsPerRun = 1,
        };

        await _runner.RunAsync(Request(client, tools, settings), CancellationToken.None);

        var seed = client.Calls[0].Messages.Last(m => m.Role == ChatRole.User).Content;
        Assert.That(seed, Does.Contain("Test card"));
        Assert.That(seed, Does.Contain("Do the thing."));
        Assert.That(seed, Does.Contain("Analyze the card."));
    }

    [Test]
    public async Task ExternalCancellation_ReturnsCancelledOutcome()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new FakeChatCompletionClient().EnqueueText("never consumed");
        var tools = new ITool[] { new StubTool("read_file") };

        var result = await _runner.RunAsync(Request(client, tools), cts.Token);

        Assert.That(result.Outcome.Succeeded, Is.False);
        Assert.That(result.Outcome.IsCancelled, Is.True);
    }

    [Test]
    public async Task Stall_FailsRetryableWithStallReason()
    {
        var client = new HangingClient();
        var tools = new ITool[] { new StubTool("read_file") };
        var settings = OrchestratorSettings.Default with
        {
            AllowedTools = new[] { "read_file" },
            StallTimeoutSeconds = 1,
            TurnTimeoutSeconds = 60,
        };

        var result = await _runner.RunAsync(Request(client, tools, settings), CancellationToken.None);

        Assert.That(result.Outcome.Succeeded, Is.False);
        Assert.That(result.Outcome.Retryable, Is.True);
        Assert.That(result.Outcome.FailureReason, Does.Contain("stalled"));
    }

    [Test]
    public async Task EndpointFailure_FailsRetryable()
    {
        var client = new ThrowingClient();
        var tools = new ITool[] { new StubTool("read_file") };

        var result = await _runner.RunAsync(Request(client, tools), CancellationToken.None);

        Assert.That(result.Outcome.Succeeded, Is.False);
        Assert.That(result.Outcome.Retryable, Is.True);
        Assert.That(result.Outcome.FailureReason, Does.Contain("connection refused"));
    }

    /// <summary>Emits nothing until cancelled — exercises the stall watchdog.</summary>
    private sealed class HangingClient : IChatCompletionClient
    {
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ChatTool> tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
            yield break;
        }
    }

    /// <summary>Fails like an unreachable endpoint.</summary>
    private sealed class ThrowingClient : IChatCompletionClient
    {
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ChatTool> tools,
            CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }
}
