using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Azure.Core;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Options;
using Lges.Ama.AgentHost;

namespace Lges.Ama.AgentHost.Tests;

[TestClass]
public sealed class AgentIdentityOboTokenExchangeTests
{
    private const string TenantId = "11111111-1111-4111-8111-111111111111";
    private const string AgentIdentityId = "22222222-2222-4222-8222-222222222222";
    private const string BlueprintId = "33333333-3333-4333-8333-333333333333";
    private const string UserAssertion = "blueprint-user-assertion";
    private static readonly string[] FoundryScopes = ["https://cognitiveservices.azure.com/.default"];

    [TestMethod]
    public async Task ExchangesParentTokenThenChildOboToken()
    {
        var parentProvider = new RecordingParentTokenProvider("child-bound-parent-token");
        var childClient = new RecordingChildTokenClient(CreateToken(AgentIdentityId));
        var exchange = new AgentIdentityOboTokenExchange(parentProvider, childClient);

        var token = await exchange.ExchangeAsync(
            TenantId,
            AgentIdentityId,
            UserAssertion,
            FoundryScopes,
            TestContext.CancellationToken);

        Assert.AreEqual(childClient.ResultToken, token);
        Assert.AreEqual(1, parentProvider.CallCount);
        Assert.AreEqual(TenantId, parentProvider.TenantId);
        Assert.AreEqual(AgentIdentityId, parentProvider.AgentIdentityId);
        Assert.AreEqual(1, childClient.CallCount);
        Assert.AreEqual("child-bound-parent-token", childClient.ParentToken);
        Assert.AreEqual(UserAssertion, childClient.UserAssertion);
        CollectionAssert.AreEqual(FoundryScopes, childClient.Scopes);
    }

    [TestMethod]
    public async Task RejectsTokenStillMintedForBlueprint()
    {
        var exchange = new AgentIdentityOboTokenExchange(
            new RecordingParentTokenProvider("child-bound-parent-token"),
            new RecordingChildTokenClient(CreateToken(BlueprintId)));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await exchange.ExchangeAsync(
                TenantId,
                AgentIdentityId,
                UserAssertion,
                FoundryScopes,
                TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task RejectsIndividualDelegatedScope()
    {
        var exchange = new AgentIdentityOboTokenExchange(
            new RecordingParentTokenProvider("child-bound-parent-token"),
            new RecordingChildTokenClient(CreateToken(AgentIdentityId)));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await exchange.ExchangeAsync(
                TenantId,
                AgentIdentityId,
                UserAssertion,
                ["https://graph.microsoft.com/Content.Process.User"],
                TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ParentProviderRequestsFmiTokenForConfiguredChild()
    {
        var tokenProvider = new RecordingAgenticTokenProvider();
        var connections = new RecordingConnections(tokenProvider);
        var provider = new AgentIdentityParentTokenProvider(
            connections,
            Options.Create(new AgentIdentityOboOptions
            {
                AgentId = AgentIdentityId,
                BlueprintConnectionName = "OboServiceConnection"
            }));

        var token = await provider.GetParentTokenAsync(
            TenantId,
            AgentIdentityId,
            TestContext.CancellationToken);

        Assert.AreEqual("child-bound-parent-token", token);
        Assert.AreEqual("OboServiceConnection", connections.RequestedConnectionName);
        Assert.AreEqual(TenantId, tokenProvider.TenantId);
        Assert.AreEqual(AgentIdentityId, tokenProvider.AgentIdentityId);
    }

    [TestMethod]
    public async Task AcquiresAppOnlyChildTokenWithoutUserAssertion()
    {
        var parentProvider = new RecordingParentTokenProvider("child-bound-parent-token");
        var childClient = new RecordingChildTokenClient(CreateToken(AgentIdentityId));
        var exchange = new AgentIdentityOboTokenExchange(parentProvider, childClient);

        var token = await exchange.AcquireAppTokenAsync(
            TenantId,
            AgentIdentityId,
            [AgentIdentityAuthorizationScopes.ObservabilityDefault],
            TestContext.CancellationToken);

        Assert.AreEqual(childClient.ResultToken, token);
        Assert.AreEqual(1, parentProvider.CallCount);
        Assert.AreEqual(1, childClient.AppCallCount);
        Assert.AreEqual(0, childClient.CallCount);
        Assert.IsNull(childClient.UserAssertion);
        CollectionAssert.AreEqual(
            new[] { AgentIdentityAuthorizationScopes.ObservabilityDefault },
            childClient.Scopes);
    }

    [TestMethod]
    public async Task RejectsAppOnlyTokenStillMintedForBlueprint()
    {
        var exchange = new AgentIdentityOboTokenExchange(
            new RecordingParentTokenProvider("child-bound-parent-token"),
            new RecordingChildTokenClient(CreateToken(BlueprintId)));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await exchange.AcquireAppTokenAsync(
                TenantId,
                AgentIdentityId,
                [AgentIdentityAuthorizationScopes.ObservabilityDefault],
                TestContext.CancellationToken));
    }

    public TestContext TestContext { get; set; } = null!;

    private static string CreateToken(string clientId) =>
        new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityToken(claims: [new Claim("azp", clientId)]));

    private sealed class RecordingParentTokenProvider(string token) : IAgentIdentityParentTokenProvider
    {
        public int CallCount { get; private set; }

        public string? TenantId { get; private set; }

        public string? AgentIdentityId { get; private set; }

        public Task<string> GetParentTokenAsync(
            string tenantId,
            string agentIdentityId,
            CancellationToken cancellationToken)
        {
            CallCount++;
            TenantId = tenantId;
            AgentIdentityId = agentIdentityId;
            return Task.FromResult(token);
        }
    }

    private sealed class RecordingChildTokenClient(string resultToken) : IAgentIdentityChildTokenClient
    {
        public string ResultToken { get; } = resultToken;

        public int CallCount { get; private set; }

        public int AppCallCount { get; private set; }

        public string? ParentToken { get; private set; }

        public string? UserAssertion { get; private set; }

        public string[] Scopes { get; private set; } = [];

        public Task<string> AcquireTokenAsync(
            string tenantId,
            string agentIdentityId,
            string parentToken,
            string userAssertion,
            IReadOnlyCollection<string> scopes,
            CancellationToken cancellationToken)
        {
            CallCount++;
            ParentToken = parentToken;
            UserAssertion = userAssertion;
            Scopes = [.. scopes];
            return Task.FromResult(ResultToken);
        }

        public Task<string> AcquireAppTokenAsync(
            string tenantId,
            string agentIdentityId,
            string parentToken,
            IReadOnlyCollection<string> scopes,
            CancellationToken cancellationToken)
        {
            AppCallCount++;
            ParentToken = parentToken;
            Scopes = [.. scopes];
            return Task.FromResult(ResultToken);
        }
    }

    private sealed class RecordingConnections(IAccessTokenProvider connection) : IConnections
    {
        public string? RequestedConnectionName { get; private set; }

        public IAccessTokenProvider GetConnection(string name)
        {
            RequestedConnectionName = name;
            return connection;
        }

        public bool TryGetConnection(string name, out IAccessTokenProvider resolvedConnection)
        {
            RequestedConnectionName = name;
            resolvedConnection = connection;
            return true;
        }

        public IAccessTokenProvider GetDefaultConnection() => connection;

        public IAccessTokenProvider GetTokenProvider(ClaimsIdentity claimsIdentity, string serviceUrl) => connection;

        public IAccessTokenProvider GetTokenProvider(ClaimsIdentity claimsIdentity, IActivity activity) => connection;
    }

    private sealed class RecordingAgenticTokenProvider : IAccessTokenProvider, IAgenticTokenProvider
    {
        public string? TenantId { get; private set; }

        public string? AgentIdentityId { get; private set; }

        public ImmutableConnectionSettings ConnectionSettings => null!;

        public Task<string> GetAgenticApplicationTokenAsync(
            string tenantId,
            string agentAppInstanceId,
            CancellationToken cancellationToken = default)
        {
            TenantId = tenantId;
            AgentIdentityId = agentAppInstanceId;
            return Task.FromResult("child-bound-parent-token");
        }

        public Task<string> GetAgenticInstanceTokenAsync(
            string tenantId,
            string agentAppInstanceId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> GetAgenticUserTokenAsync(
            string tenantId,
            string agentAppInstanceId,
            string upn,
            IList<string> scopes,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> GetAccessTokenAsync(
            string resourceUrl,
            IList<string> scopes,
            bool forceRefresh = false) =>
            throw new NotSupportedException();

        public TokenCredential GetTokenCredential() => throw new NotSupportedException();
    }
}
