using Lges.Ama.AgentHost;

namespace Lges.Ama.AgentHost.Tests;

[TestClass]
public sealed class AgentTurnCoordinatorTests
{
    [TestMethod]
    public async Task SuppressesCompletedActivityReplay()
    {
        var coordinator = new AgentTurnCoordinator(8);
        using (var first = await coordinator.EnterAsync(
            AgentFrontendMode.OnBehalfOf,
            "conversation",
            "activity",
            TestContext.CancellationToken))
        {
            Assert.IsFalse(first.IsDuplicate);
        }

        using var replay = await coordinator.EnterAsync(
            AgentFrontendMode.OnBehalfOf,
            "conversation",
            "activity",
            TestContext.CancellationToken);

        Assert.IsTrue(replay.IsDuplicate);
    }

    [TestMethod]
    public async Task KeepsFrontendReplayBoundariesSeparate()
    {
        var coordinator = new AgentTurnCoordinator(8);
        using var obo = await coordinator.EnterAsync(
            AgentFrontendMode.OnBehalfOf,
            "conversation",
            "activity",
            TestContext.CancellationToken);
        obo.Dispose();

        using var agentic = await coordinator.EnterAsync(
            AgentFrontendMode.AgenticUser,
            "conversation",
            "activity",
            TestContext.CancellationToken);

        Assert.IsFalse(agentic.IsDuplicate);
    }

    [TestMethod]
    public async Task SerializesConcurrentTurnsForConversation()
    {
        var coordinator = new AgentTurnCoordinator(8);
        using var first = await coordinator.EnterAsync(
            AgentFrontendMode.OnBehalfOf,
            "conversation",
            "activity-1",
            TestContext.CancellationToken);
        var secondTask = coordinator.EnterAsync(
            AgentFrontendMode.OnBehalfOf,
            "conversation",
            "activity-2",
            TestContext.CancellationToken);

        Assert.IsFalse(secondTask.IsCompleted);
        first.Dispose();
        using var second = await secondTask.WaitAsync(TestContext.CancellationToken);

        Assert.IsFalse(second.IsDuplicate);
    }

    [TestMethod]
    public async Task WaitingTurnPreservesCallerCancellation()
    {
        var coordinator = new AgentTurnCoordinator(8);
        using var first = await coordinator.EnterAsync(
            AgentFrontendMode.OnBehalfOf,
            "conversation",
            "activity-1",
            TestContext.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
            coordinator.EnterAsync(
                AgentFrontendMode.OnBehalfOf,
                "conversation",
                "activity-2",
                cancellation.Token));
    }

    [TestMethod]
    public async Task EvictsOldestCompletedActivityAtCapacity()
    {
        var coordinator = new AgentTurnCoordinator(1);
        using (var first = await coordinator.EnterAsync(
            AgentFrontendMode.OnBehalfOf,
            "conversation",
            "activity-1",
            TestContext.CancellationToken))
        {
            Assert.IsFalse(first.IsDuplicate);
        }
        using (var second = await coordinator.EnterAsync(
            AgentFrontendMode.OnBehalfOf,
            "conversation",
            "activity-2",
            TestContext.CancellationToken))
        {
            Assert.IsFalse(second.IsDuplicate);
        }

        using var replay = await coordinator.EnterAsync(
            AgentFrontendMode.OnBehalfOf,
            "conversation",
            "activity-1",
            TestContext.CancellationToken);

        Assert.IsFalse(replay.IsDuplicate);
    }

    public TestContext TestContext { get; set; } = null!;
}
