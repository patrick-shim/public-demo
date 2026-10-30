using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;
using Lges.Ama.AgentHost;

namespace Lges.Ama.AgentHost.Tests;

[TestClass]
public sealed class AgentIdentityBearerTokenHandlerTests
{
    private const string Audience = "api://11111111-1111-4111-8111-111111111111";

    [TestMethod]
    public async Task AddsDelegatedMcpTokenFromCurrentAgentIdentityTurn()
    {
        var context = new AgentIdentityTokenContext();
        var recorder = new RecordingHandler();
        using var handler = CreateHandler(context, recorder);
        using var client = new HttpClient(handler);
        using var scope = context.Push(CreateTokens("agent-a", "mcp-a"));

        using var response = await client.GetAsync(
            "https://internal.example/agent-a",
            TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("Bearer mcp-a", recorder.Authorizations["agent-a"]);
    }

    [TestMethod]
    public async Task ConcurrentRequestsUseTheirOwnAgentIdentityToken()
    {
        var context = new AgentIdentityTokenContext();
        var recorder = new RecordingHandler();
        using var handler = CreateHandler(context, recorder);
        using var client = new HttpClient(handler);
        var bothReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;

        async Task SendAsync(string agentId, string token)
        {
            using var scope = context.Push(CreateTokens(agentId, token));
            if (Interlocked.Increment(ref readyCount) == 2)
            {
                bothReady.SetResult();
            }

            await bothReady.Task.WaitAsync(TestContext.CancellationToken);
            using var response = await client.GetAsync(
                $"https://internal.example/{agentId}",
                TestContext.CancellationToken);
            response.EnsureSuccessStatusCode();
        }

        await Task.WhenAll(
            Task.Run(() => SendAsync("agent-a", "mcp-a"), TestContext.CancellationToken),
            Task.Run(() => SendAsync("agent-b", "mcp-b"), TestContext.CancellationToken));

        Assert.AreEqual("Bearer mcp-a", recorder.Authorizations["agent-a"]);
        Assert.AreEqual("Bearer mcp-b", recorder.Authorizations["agent-b"]);
    }

    public TestContext TestContext { get; set; } = null!;

    private static AgentIdentityBearerTokenHandler CreateHandler(
        AgentIdentityTokenContext context,
        HttpMessageHandler innerHandler) =>
        new(
            context,
            Options.Create(new InternalMcpOptions
            {
                UseAuthentication = true,
                Audience = Audience
            }))
        {
            InnerHandler = innerHandler
        };

    private static AgentIdentityTurnTokens CreateTokens(string agentId, string token) =>
        new(
            agentId,
            "22222222-2222-4222-8222-222222222222",
            new Dictionary<string, string>
            {
                [AgentIdentityAuthorizationScopes.InternalMcp(Audience)] = token
            });

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public ConcurrentDictionary<string, string?> Authorizations { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorizations[request.RequestUri!.Segments[^1]] = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
