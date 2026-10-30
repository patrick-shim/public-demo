using System.Reflection;
using Lges.Ama.AgentHost;

namespace Lges.Ama.AgentHost.Tests;

[TestClass]
public sealed class AgentTurnFrontendContextTests
{
    private static readonly AgentFrontendMode[] ExpectedModes =
        [AgentFrontendMode.AgenticUser, AgentFrontendMode.OnBehalfOf];

    [TestMethod]
    public async Task ConcurrentTurnsKeepTheirOwnFrontendMode()
    {
        var context = new AgentTurnFrontendContext();
        var bothReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;

        async Task<AgentFrontendMode> ResolveAsync(AgentFrontendMode mode)
        {
            using var scope = context.Push(mode);
            if (Interlocked.Increment(ref readyCount) == 2)
            {
                bothReady.SetResult();
            }

            await bothReady.Task.WaitAsync(TestContext.CancellationToken);
            await Task.Yield();
            return context.ResolveForActivity(mode, isLocalEnvironment: false);
        }

        var modes = await Task.WhenAll(
            Task.Run(
                () => ResolveAsync(AgentFrontendMode.AgenticUser),
                TestContext.CancellationToken),
            Task.Run(
                () => ResolveAsync(AgentFrontendMode.OnBehalfOf),
                TestContext.CancellationToken));

        CollectionAssert.AreEquivalent(ExpectedModes, modes);
        Assert.IsNull(context.Current);
    }

    [TestMethod]
    public void ProductionTurnRejectsRouteAndActivityModeMismatch()
    {
        var context = new AgentTurnFrontendContext();
        using var scope = context.Push(AgentFrontendMode.AgenticUser);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            context.ResolveForActivity(
                AgentFrontendMode.OnBehalfOf,
                isLocalEnvironment: false));
    }

    [TestMethod]
    public void ProductionContinuationUsesRegisteredActivityModeWhenRouteContextDoesNotFlow()
    {
        var context = new AgentTurnFrontendContext();

        Assert.AreEqual(
            AgentFrontendMode.OnBehalfOf,
            context.ResolveForActivity(
                AgentFrontendMode.OnBehalfOf,
                isLocalEnvironment: false));
    }

    [TestMethod]
    public void PlaygroundAllowsAnonymousActivityShape()
    {
        var context = new AgentTurnFrontendContext();

        Assert.AreEqual(
            AgentFrontendMode.OnBehalfOf,
            context.ResolveForActivity(
                AgentFrontendMode.OnBehalfOf,
                isLocalEnvironment: true));
    }

    [TestMethod]
    public void FrontendsUseDistinctConversationSessionKeys()
    {
        var resolver = typeof(LgesAmaApplication).GetMethod(
            "GetConversationSessionKey",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new AssertFailedException("Conversation session key resolver was not found.");

        var agenticKey = resolver.Invoke(null, [AgentFrontendMode.AgenticUser]);
        var oboKey = resolver.Invoke(null, [AgentFrontendMode.OnBehalfOf]);

        Assert.AreNotEqual(agenticKey, oboKey);
    }

    public TestContext TestContext { get; set; } = null!;
}
