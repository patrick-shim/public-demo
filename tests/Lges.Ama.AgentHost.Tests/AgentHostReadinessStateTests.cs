using Lges.Ama.AgentHost;

namespace Lges.Ama.AgentHost.Tests;

[TestClass]
public sealed class AgentHostReadinessStateTests
{
    [TestMethod]
    public void BecomesReadyOnlyAfterStartupCompletes()
    {
        var state = new AgentHostReadinessState();

        Assert.IsFalse(state.IsReady);
        state.MarkReady(hasDurableStorage: false);
        Assert.IsTrue(state.IsReady);
        Assert.IsFalse(state.HasDurableStorage);
    }

    [TestMethod]
    public void RecordsDurableStorageReadiness()
    {
        var state = new AgentHostReadinessState();

        state.MarkReady(hasDurableStorage: true);

        Assert.IsTrue(state.IsReady);
        Assert.IsTrue(state.HasDurableStorage);
    }
}
