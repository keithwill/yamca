using NUnit.Framework;
using Yamca.Agent.Permissions;
using Yamca.Agent.Tests.Support;

namespace Yamca.Agent.Tests.Permissions;

[TestFixture]
public class ApprovalCoordinatorTests
{
    [Test]
    public async Task ApprovedRequest_ResolvesWithApprovedDecision()
    {
        var coordinator = new ApprovalCoordinator();

        var requestTask = coordinator.RequestApprovalAsync(
            "write_file",
            Json.Parse("""{ "path": "out.txt" }"""),
            CancellationToken.None);

        // Reader picks up the pending request.
        var pending = await coordinator.Pending.ReadAsync();
        Assert.That(pending.ToolName, Is.EqualTo("write_file"));

        pending.Approve(ApprovalPersistence.Project);

        var decision = await requestTask;
        Assert.That(decision.Approved, Is.True);
        Assert.That(decision.Persistence, Is.EqualTo(ApprovalPersistence.Project));
    }

    [Test]
    public async Task DeniedRequest_ResolvesWithDeniedDecision()
    {
        var coordinator = new ApprovalCoordinator();

        var requestTask = coordinator.RequestApprovalAsync(
            "delete_file",
            Json.Parse("""{ "path": "x" }"""),
            CancellationToken.None);

        var pending = await coordinator.Pending.ReadAsync();
        pending.Deny(ApprovalPersistence.None);

        var decision = await requestTask;
        Assert.That(decision.Approved, Is.False);
        Assert.That(decision.Persistence, Is.EqualTo(ApprovalPersistence.None));
    }

    [Test]
    public void Cancellation_AbandonsRequest()
    {
        var coordinator = new ApprovalCoordinator();
        using var cts = new CancellationTokenSource();

        var requestTask = coordinator.RequestApprovalAsync(
            "write_file",
            Json.Parse("""{ "path": "x" }"""),
            cts.Token);

        cts.Cancel();

        Assert.ThrowsAsync<TaskCanceledException>(async () => await requestTask);
    }

    [Test]
    public async Task MultipleRequests_DeliveredInFifoOrder()
    {
        var coordinator = new ApprovalCoordinator();

        var t1 = coordinator.RequestApprovalAsync("a", Json.Parse("{}"), CancellationToken.None);
        var t2 = coordinator.RequestApprovalAsync("b", Json.Parse("{}"), CancellationToken.None);

        var first = await coordinator.Pending.ReadAsync();
        var second = await coordinator.Pending.ReadAsync();
        Assert.That(first.ToolName, Is.EqualTo("a"));
        Assert.That(second.ToolName, Is.EqualTo("b"));

        second.Approve();
        first.Deny();

        Assert.That((await t1).Approved, Is.False);
        Assert.That((await t2).Approved, Is.True);
    }
}
