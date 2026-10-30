using Azure.Core;
using Microsoft.Extensions.Options;
using Lges.Ama.AgentHost;

namespace Lges.Ama.AgentHost.Tests;

[TestClass]
public sealed class AgentIdentityTokenContextTests
{
    private static readonly string[] FoundryScopes = [AgentIdentityAuthorizationScopes.Foundry];
    private static readonly string[] GraphScopes = [AgentIdentityAuthorizationScopes.GraphDefault];
    private static readonly string[] ExpectedConcurrentTokens = ["foundry-a", "foundry-b"];

    [TestMethod]
    public async Task CredentialRoutesTokensByRequestedResourceScope()
    {
        var context = new AgentIdentityTokenContext();
        var credential = new AgentIdentityTokenCredential(context);
        using var scope = context.Push(CreateTokens("agent-a", "foundry-a", "graph-a"));

        var foundry = await credential.GetTokenAsync(
            new TokenRequestContext(FoundryScopes),
            TestContext.CancellationToken);
        var purview = await credential.GetTokenAsync(
            new TokenRequestContext(GraphScopes),
            TestContext.CancellationToken);

        Assert.AreEqual("foundry-a", foundry.Token);
        Assert.AreEqual("graph-a", purview.Token);
    }

    [TestMethod]
    public async Task CredentialAcceptsCompletePurviewDelegatedScopeSet()
    {
        var context = new AgentIdentityTokenContext();
        var credential = new AgentIdentityTokenCredential(context);
        using var scope = context.Push(CreateTokens("agent-a", "foundry-a", "graph-a"));

        var purview = await credential.GetTokenAsync(
            new TokenRequestContext(AgentIdentityAuthorizationScopes.PurviewDelegated),
            TestContext.CancellationToken);

        Assert.AreEqual("graph-a", purview.Token);
    }

    [TestMethod]
    public async Task PurviewCredentialMapsOnlyConfiguredLoopbackScopeToGraph()
    {
        var context = new AgentIdentityTokenContext();
        var credential = new PurviewAgentIdentityTokenCredential(
            context,
            Options.Create(new PurviewDlpOptions
            {
                UseCompatibilityProxy = true,
                CompatibilityProxyBaseUri = new Uri("http://127.0.0.1:8080/internal/purview/")
            }));
        using var scope = context.Push(CreateTokens("agent-a", "foundry-a", "graph-a"));

        var token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://127.0.0.1/.default"]),
            TestContext.CancellationToken);

        Assert.AreEqual("graph-a", token.Token);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await credential.GetTokenAsync(
                new TokenRequestContext(["https://localhost/.default"]),
                TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ConcurrentTurnsDoNotReuseAnotherAgentIdentityToken()
    {
        var context = new AgentIdentityTokenContext();
        var credential = new AgentIdentityTokenCredential(context);
        var bothReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;

        async Task<string> ResolveAsync(string agentId, string token)
        {
            using var scope = context.Push(CreateTokens(agentId, token, $"graph-{agentId}"));
            if (Interlocked.Increment(ref readyCount) == 2)
            {
                bothReady.SetResult();
            }

            await bothReady.Task.WaitAsync(TestContext.CancellationToken);
            await Task.Yield();
            var accessToken = await credential.GetTokenAsync(
                new TokenRequestContext(FoundryScopes),
                TestContext.CancellationToken);
            return accessToken.Token;
        }

        var results = await Task.WhenAll(
            Task.Run(() => ResolveAsync("agent-a", "foundry-a"), TestContext.CancellationToken),
            Task.Run(() => ResolveAsync("agent-b", "foundry-b"), TestContext.CancellationToken));

        CollectionAssert.AreEquivalent(ExpectedConcurrentTokens, results);
        Assert.IsNull(context.Current);
    }

    [TestMethod]
    public async Task CredentialFailsClosedWithoutActiveTurn()
    {
        var credential = new AgentIdentityTokenCredential(new AgentIdentityTokenContext());

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await credential.GetTokenAsync(
                new TokenRequestContext(FoundryScopes),
                TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task CredentialRejectsRequestsThatSpanResourceTokens()
    {
        var context = new AgentIdentityTokenContext();
        var credential = new AgentIdentityTokenCredential(context);
        using var scope = context.Push(CreateTokens("agent-a", "foundry-a", "graph-a"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await credential.GetTokenAsync(
                new TokenRequestContext(
                    [
                        AgentIdentityAuthorizationScopes.Foundry,
                        AgentIdentityAuthorizationScopes.GraphDefault
                    ]),
                TestContext.CancellationToken));
    }

    public TestContext TestContext { get; set; } = null!;

    private static AgentIdentityTurnTokens CreateTokens(
        string agentId,
        string foundryToken,
        string purviewToken) =>
        new(
            agentId,
            "11111111-1111-4111-8111-111111111111",
            new Dictionary<string, string>
            {
                [AgentIdentityAuthorizationScopes.Foundry] = foundryToken,
                [AgentIdentityAuthorizationScopes.Purview] = purviewToken,
                [AgentIdentityAuthorizationScopes.PurviewProtectionScopes] = purviewToken,
                [AgentIdentityAuthorizationScopes.PurviewContentActivity] = purviewToken,
                [AgentIdentityAuthorizationScopes.GraphDefault] = purviewToken
            });
}
