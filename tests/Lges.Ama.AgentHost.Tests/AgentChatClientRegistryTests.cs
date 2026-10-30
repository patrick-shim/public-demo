using Microsoft.Extensions.AI;
using Lges.Ama.AgentHost;

namespace Lges.Ama.AgentHost.Tests;

[TestClass]
public sealed class AgentChatClientRegistryTests
{
    [TestMethod]
    [TestCategory("Purview")]
    public void CreatesDistinctClientsForEveryTurnWithoutEarlyDisposal()
    {
        var clients = new List<DisposableChatClient>();
        using var registry = new AgentChatClientRegistry(applicationId =>
        {
            var client = new DisposableChatClient(applicationId);
            clients.Add(client);
            return client;
        });

        var first = registry.Create("11111111-1111-4111-8111-111111111111");
        var second = registry.Create("11111111-1111-4111-8111-111111111111");

        Assert.AreNotSame(first, second);
        Assert.HasCount(2, clients);
        Assert.IsTrue(clients.All(client => client.DisposeCount == 0));
    }

    [TestMethod]
    [TestCategory("Purview")]
    public void KeepsFrontendLocationsIsolatedAndDisposesEachClientOnce()
    {
        var clients = new List<DisposableChatClient>();
        var registry = new AgentChatClientRegistry(applicationId =>
        {
            var client = new DisposableChatClient(applicationId);
            clients.Add(client);
            return client;
        });

        var agentic = registry.Create("11111111-1111-4111-8111-111111111111");
        var obo = registry.Create("22222222-2222-4222-8222-222222222222");

        Assert.AreNotSame(agentic, obo);
        Assert.HasCount(2, clients);

        registry.Dispose();
        registry.Dispose();

        Assert.IsTrue(clients.All(client => client.DisposeCount == 1));
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            registry.Create("33333333-3333-4333-8333-333333333333"));
    }

    private sealed class DisposableChatClient(string applicationId) : IChatClient
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(applicationId);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(applicationId);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
