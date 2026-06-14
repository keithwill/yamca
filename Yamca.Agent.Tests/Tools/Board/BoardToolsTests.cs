using NUnit.Framework;
using Yamca.Agent.Board;
using Yamca.Agent.Permissions;
using Yamca.Agent.Storage;
using Yamca.Agent.Tests.Support;
using Yamca.Agent.Tools;
using Yamca.Agent.Tools.Board;

namespace Yamca.Agent.Tests.Tools.Board;

[TestFixture]
public class BoardToolsTests
{
    private TempWorkspace _ws = null!;
    private BoardStore _boardStore = null!;
    private string _idea = null!;
    private string _plan = null!;

    [SetUp]
    public async Task SetUp()
    {
        _ws = new TempWorkspace();
        // The board is backed by an in-memory store; the workspace only serves the ToolContext.
        _boardStore = new BoardStore(new YamcaStore(filePath: null));
        var snap = await _boardStore.ReadAsync(CancellationToken.None);
        _idea = snap.FindColumn("idea")!.Id;
        _plan = snap.FindColumn("plan")!.Id;
    }

    [TearDown]
    public void TearDown() => _ws.Dispose();

    private ToolContext Ctx() => new(_ws.Workspace, restrictToWorkspace: true);

    [Test]
    public async Task BoardList_FormatsColumnsCardsAndProgress()
    {
        await _boardStore.AddCardAsync(_idea, "Add OAuth", "the ask", "feat/oauth", CardPriority.Normal, CancellationToken.None);
        await _boardStore.AddTasksAsync(1, new[] { "a", "b" }, CancellationToken.None);
        await _boardStore.SetTaskDoneAsync(1, 1, true, CancellationToken.None);

        var result = await new BoardListTool(_boardStore).ExecuteAsync(
            Json.Parse("{}"), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("## idea"));
        Assert.That(result.Content, Does.Contain("#1 Add OAuth"));
        Assert.That(result.Content, Does.Contain("[1/2]"));
        Assert.That(result.Content, Does.Contain("branch: feat/oauth"));
        Assert.That(result.Content, Does.Contain("## plan"));
    }

    [Test]
    public async Task BoardGetCard_ReturnsRenderedMarkdown_AndListsTasksWithIds()
    {
        await _boardStore.AddCardAsync(_idea, "OAuth", "# Body\ntext", null, CardPriority.Normal, CancellationToken.None);
        await _boardStore.AddTasksAsync(1, new[] { "write tests", "ship it" }, CancellationToken.None);
        await _boardStore.SetTaskDoneAsync(1, 2, true, CancellationToken.None);

        var result = await new BoardGetCardTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("# Body"));
        Assert.That(result.Content, Does.Contain("id: 1"));
        Assert.That(result.Content, Does.Contain("in column 'idea'"));
        // Tasks are listed (read-only) with their ids and done state, off the body.
        Assert.That(result.Content, Does.Contain("- #1 [ ] write tests"));
        Assert.That(result.Content, Does.Contain("- #2 [x] ship it"));
    }

    [Test]
    public async Task BoardGetCard_UnknownCard_Error()
    {
        var result = await new BoardGetCardTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "999" }"""), Ctx(), CancellationToken.None);
        Assert.That(result.IsError, Is.True);
    }

    [Test]
    public async Task BoardGetStepInstructions_ReturnsContent_AndNoteWhenMissing()
    {
        var present = await new BoardGetStepInstructionsTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "column": "implement" }"""), Ctx(), CancellationToken.None);
        Assert.That(present.IsError, Is.False, present.Content);
        Assert.That(present.Content, Does.Contain("implementation work"));

        var missing = await new BoardGetStepInstructionsTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "column": "done" }"""), Ctx(), CancellationToken.None);
        Assert.That(missing.IsError, Is.False);
        Assert.That(missing.Content, Does.Contain("no step instructions"));
    }

    [Test]
    public async Task BoardMoveCard_RewritesColumn()
    {
        await _boardStore.AddCardAsync(_idea, "OAuth", "# Card", null, CardPriority.Normal, CancellationToken.None);

        var result = await new BoardMoveCardTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1", "to_column": "plan" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        var snap = await _boardStore.ReadAsync(CancellationToken.None);
        Assert.That(snap.FindCard("0001")!.ColumnId, Is.EqualTo(_plan));
    }

    [Test]
    public async Task BoardMoveCard_UnknownColumn_Error()
    {
        await _boardStore.AddCardAsync(_idea, "OAuth", "# Card", null, CardPriority.Normal, CancellationToken.None);
        var result = await new BoardMoveCardTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1", "to_column": "nope" }"""), Ctx(), CancellationToken.None);
        Assert.That(result.IsError, Is.True);
    }

    [Test]
    public async Task BoardMoveCard_Next_AdvancesOneColumn()
    {
        await _boardStore.AddCardAsync(_idea, "OAuth", "# Card", null, CardPriority.Normal, CancellationToken.None);

        var result = await new BoardMoveCardTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1", "to_column": "next" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        var snap = await _boardStore.ReadAsync(CancellationToken.None);
        // idea -> plan is the first forward step.
        Assert.That(snap.FindCard(1)!.ColumnId, Is.EqualTo(_plan));
    }

    [Test]
    public async Task BoardMoveCard_Previous_MovesBackOneColumn()
    {
        await _boardStore.AddCardAsync(_plan, "OAuth", "# Card", null, CardPriority.Normal, CancellationToken.None);

        var result = await new BoardMoveCardTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1", "to_column": "previous" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        var snap = await _boardStore.ReadAsync(CancellationToken.None);
        Assert.That(snap.FindCard(1)!.ColumnId, Is.EqualTo(_idea));
    }

    [Test]
    public async Task BoardMoveCard_Next_AtLastColumn_IsNoOp()
    {
        var snap0 = await _boardStore.ReadAsync(CancellationToken.None);
        var done = snap0.FindColumn("done")!.Id;
        await _boardStore.AddCardAsync(done, "OAuth", "# Card", null, CardPriority.Normal, CancellationToken.None);

        var result = await new BoardMoveCardTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1", "to_column": "next" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("last column"));
        var snap = await _boardStore.ReadAsync(CancellationToken.None);
        Assert.That(snap.FindCard(1)!.ColumnId, Is.EqualTo(done));
    }

    [Test]
    public async Task BoardMoveCard_Previous_AtFirstColumn_IsNoOp()
    {
        await _boardStore.AddCardAsync(_idea, "OAuth", "# Card", null, CardPriority.Normal, CancellationToken.None);

        var result = await new BoardMoveCardTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1", "to_column": "previous" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("first column"));
        var snap = await _boardStore.ReadAsync(CancellationToken.None);
        Assert.That(snap.FindCard(1)!.ColumnId, Is.EqualTo(_idea));
    }

    [Test]
    public async Task BoardUpdateCard_AppliesContent()
    {
        await _boardStore.AddCardAsync(_idea, "old", "old", null, CardPriority.Normal, CancellationToken.None);

        var result = await new BoardUpdateCardTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1", "content": "new content\n- [x] done" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        var card = (await _boardStore.ReadAsync(CancellationToken.None)).FindCard("0001")!;
        // Checklist-looking lines stay in the body verbatim; the card has no tasks (those use the task tools).
        Assert.That(card.Body, Is.EqualTo("new content\n- [x] done"));
        Assert.That(card.Tasks, Is.Empty);
    }

    [Test]
    public async Task BoardAddTasks_AppendsWithIds_AndReportsThem()
    {
        await _boardStore.AddCardAsync(_idea, "OAuth", "ask", null, CardPriority.Normal, CancellationToken.None);

        var result = await new BoardAddTasksTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1", "tasks": ["write tests", "ship it"] }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That(result.Content, Does.Contain("- #1 [ ] write tests"));
        Assert.That(result.Content, Does.Contain("- #2 [ ] ship it"));
        var card = (await _boardStore.ReadAsync(CancellationToken.None)).FindCard("0001")!;
        Assert.That(card.Tasks.Select(t => t.Text), Is.EqualTo(new[] { "write tests", "ship it" }));
    }

    [Test]
    public async Task BoardCompleteTask_TicksByIdAndUnticksWithDoneFalse()
    {
        await _boardStore.AddCardAsync(_idea, "OAuth", "ask", null, CardPriority.Normal, CancellationToken.None);
        await _boardStore.AddTasksAsync(1, new[] { "a" }, CancellationToken.None);

        var tick = await new BoardCompleteTaskTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1", "task": 1 }"""), Ctx(), CancellationToken.None);
        Assert.That(tick.IsError, Is.False, tick.Content);
        Assert.That((await _boardStore.ReadAsync(CancellationToken.None)).FindCard("0001")!.Tasks.Single().Done, Is.True);

        var untick = await new BoardCompleteTaskTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1", "task": 1, "done": false }"""), Ctx(), CancellationToken.None);
        Assert.That(untick.IsError, Is.False, untick.Content);
        Assert.That((await _boardStore.ReadAsync(CancellationToken.None)).FindCard("0001")!.Tasks.Single().Done, Is.False);

        var missing = await new BoardCompleteTaskTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1", "task": 99 }"""), Ctx(), CancellationToken.None);
        Assert.That(missing.IsError, Is.True);
    }

    [Test]
    public async Task BoardUpdateTask_RewritesText()
    {
        await _boardStore.AddCardAsync(_idea, "OAuth", "ask", null, CardPriority.Normal, CancellationToken.None);
        await _boardStore.AddTasksAsync(1, new[] { "old" }, CancellationToken.None);

        var result = await new BoardUpdateTaskTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1", "task": 1, "text": "new" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That((await _boardStore.ReadAsync(CancellationToken.None)).FindCard("0001")!.Tasks.Single().Text, Is.EqualTo("new"));
    }

    [Test]
    public async Task BoardRemoveTask_DeletesById()
    {
        await _boardStore.AddCardAsync(_idea, "OAuth", "ask", null, CardPriority.Normal, CancellationToken.None);
        await _boardStore.AddTasksAsync(1, new[] { "a", "b" }, CancellationToken.None);

        var result = await new BoardRemoveTaskTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1", "task": 1 }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That((await _boardStore.ReadAsync(CancellationToken.None)).FindCard("0001")!.Tasks.Select(t => t.Id),
            Is.EqualTo(new[] { 2 }));

        var missing = await new BoardRemoveTaskTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1", "task": 99 }"""), Ctx(), CancellationToken.None);
        Assert.That(missing.IsError, Is.True);
    }

    [Test]
    public async Task BoardSetArtifact_StoresContent_OffTheBody()
    {
        await _boardStore.AddCardAsync(_idea, "OAuth", "the ask", null, CardPriority.Normal, CancellationToken.None);

        var result = await new BoardSetArtifactTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1", "kind": "plan", "content": "do A then B" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        var card = (await _boardStore.ReadAsync(CancellationToken.None)).FindCard("0001")!;
        Assert.That(card.Body, Is.EqualTo("the ask"));
        Assert.That(card.FindArtifact("plan")!.Content, Is.EqualTo("do A then B"));
    }

    [Test]
    public async Task BoardSetArtifact_BlankContent_Removes()
    {
        await _boardStore.AddCardAsync(_idea, "OAuth", "ask", null, CardPriority.Normal, CancellationToken.None);
        await _boardStore.SetArtifactAsync(1, "plan", "x", CancellationToken.None);

        var result = await new BoardSetArtifactTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1", "kind": "plan", "content": "" }"""), Ctx(), CancellationToken.None);

        Assert.That(result.IsError, Is.False, result.Content);
        Assert.That((await _boardStore.ReadAsync(CancellationToken.None)).FindCard("0001")!.Artifacts, Is.Empty);
    }

    [Test]
    public async Task BoardSetArtifact_BlankKind_Error()
    {
        await _boardStore.AddCardAsync(_idea, "OAuth", "ask", null, CardPriority.Normal, CancellationToken.None);
        var result = await new BoardSetArtifactTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1", "kind": "  ", "content": "x" }"""), Ctx(), CancellationToken.None);
        Assert.That(result.IsError, Is.True);
    }

    [Test]
    public async Task BoardGetArtifact_ReturnsContent_AndListsKindsWithoutKind()
    {
        await _boardStore.AddCardAsync(_idea, "OAuth", "ask", null, CardPriority.Normal, CancellationToken.None);
        await _boardStore.SetArtifactAsync(1, "plan", "the plan body", CancellationToken.None);

        var fetched = await new BoardGetArtifactTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1", "kind": "plan" }"""), Ctx(), CancellationToken.None);
        Assert.That(fetched.IsError, Is.False, fetched.Content);
        Assert.That(fetched.Content, Does.Contain("the plan body"));

        var listed = await new BoardGetArtifactTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1" }"""), Ctx(), CancellationToken.None);
        Assert.That(listed.IsError, Is.False);
        Assert.That(listed.Content, Does.Contain("plan"));

        var missing = await new BoardGetArtifactTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1", "kind": "nope" }"""), Ctx(), CancellationToken.None);
        Assert.That(missing.IsError, Is.True);
    }

    [Test]
    public async Task BoardGetCard_ListsArtifactKinds_AndInlinesRequested()
    {
        await _boardStore.AddCardAsync(_idea, "OAuth", "the ask", null, CardPriority.Normal, CancellationToken.None);
        await _boardStore.SetArtifactAsync(1, "plan", "PLAN BODY", CancellationToken.None);
        await _boardStore.SetArtifactAsync(1, "build-log", "LOG BODY", CancellationToken.None);

        // Without the artifacts param, kinds are advertised but content stays out of the result.
        var bare = await new BoardGetCardTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1" }"""), Ctx(), CancellationToken.None);
        Assert.That(bare.IsError, Is.False, bare.Content);
        Assert.That(bare.Content, Does.Contain("plan"));
        Assert.That(bare.Content, Does.Contain("build-log"));
        Assert.That(bare.Content, Does.Not.Contain("PLAN BODY"));

        // Naming a kind inlines just that one; the rest are still only listed.
        var withPlan = await new BoardGetCardTool(_boardStore).ExecuteAsync(
            Json.Parse("""{ "card": "1", "artifacts": ["plan"] }"""), Ctx(), CancellationToken.None);
        Assert.That(withPlan.Content, Does.Contain("PLAN BODY"));
        Assert.That(withPlan.Content, Does.Not.Contain("LOG BODY"));
        Assert.That(withPlan.Content, Does.Contain("build-log"));
    }

    [Test]
    public void PermissionDefaults_ReadsAllow_MutationsAsk()
    {
        Assert.That(new BoardListTool(_boardStore).DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(new BoardGetCardTool(_boardStore).DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(new BoardGetStepInstructionsTool(_boardStore).DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(new BoardGetArtifactTool(_boardStore).DefaultPermission, Is.EqualTo(PermissionLevel.Allow));
        Assert.That(new BoardMoveCardTool(_boardStore).DefaultPermission, Is.EqualTo(PermissionLevel.Ask));
        Assert.That(new BoardUpdateCardTool(_boardStore).DefaultPermission, Is.EqualTo(PermissionLevel.Ask));
        Assert.That(new BoardSetArtifactTool(_boardStore).DefaultPermission, Is.EqualTo(PermissionLevel.Ask));
        Assert.That(new BoardAddTasksTool(_boardStore).DefaultPermission, Is.EqualTo(PermissionLevel.Ask));
        Assert.That(new BoardCompleteTaskTool(_boardStore).DefaultPermission, Is.EqualTo(PermissionLevel.Ask));
        Assert.That(new BoardUpdateTaskTool(_boardStore).DefaultPermission, Is.EqualTo(PermissionLevel.Ask));
        Assert.That(new BoardRemoveTaskTool(_boardStore).DefaultPermission, Is.EqualTo(PermissionLevel.Ask));
    }
}
