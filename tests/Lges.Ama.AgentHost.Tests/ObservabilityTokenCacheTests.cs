using Microsoft.Agents.A365.Observability.Hosting.Caching;

namespace Lges.Ama.AgentHost.Tests;

[TestClass]
public sealed class ObservabilityTokenCacheTests
{
    private const string TenantId = "11111111-1111-4111-8111-111111111111";
    private const string OtherTenantId = "22222222-2222-4222-8222-222222222222";
    private const string AgenticAgentId = "33333333-3333-4333-8333-333333333333";
    private const string OboAgentId = "44444444-4444-4444-8444-444444444444";
    private static readonly string[] ObservabilityScopes = ["api://observability/.default"];

    [TestMethod]
    public async Task ConcurrentFrontendsRemainIsolatedByAgentAndTenant()
    {
        using var cache = new ServiceTokenCache();
        await Task.WhenAll(
            Task.Run(() => cache.RegisterObservability(
                AgenticAgentId,
                TenantId,
                "agentic-token",
                ObservabilityScopes)),
            Task.Run(() => cache.RegisterObservability(
                OboAgentId,
                TenantId,
                "obo-token",
                ObservabilityScopes)),
            Task.Run(() => cache.RegisterObservability(
                AgenticAgentId,
                OtherTenantId,
                "other-tenant-token",
                ObservabilityScopes)));

        var tokens = await Task.WhenAll(
            cache.GetObservabilityToken(AgenticAgentId, TenantId),
            cache.GetObservabilityToken(OboAgentId, TenantId),
            cache.GetObservabilityToken(AgenticAgentId, OtherTenantId));

        Assert.AreEqual("agentic-token", tokens[0]);
        Assert.AreEqual("obo-token", tokens[1]);
        Assert.AreEqual("other-tenant-token", tokens[2]);
        Assert.AreEqual(3, cache.Count);
    }
}
