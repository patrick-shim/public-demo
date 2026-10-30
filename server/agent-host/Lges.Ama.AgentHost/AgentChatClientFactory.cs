using System.ClientModel.Primitives;
using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.AI.Purview;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Responses;

namespace Lges.Ama.AgentHost;

public sealed class AgentChatClientFactory : IDisposable
{
    private readonly AgentHostOptions _agentHostOptions;
    private readonly PurviewDlpOptions _purviewOptions;
    private readonly InternalMcpOptions _internalMcpOptions;
    private readonly AgentIdentityTokenContext _tokenContext;
    private readonly PurviewAgentIdentityTokenCredential _purviewCredential;
    private readonly ToolContentProtector _toolContentProtector;
    private readonly PurviewApplicationLocationResolver _purviewApplicationLocation;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TokenCredential? _localCredential;
    private readonly AgentChatClientRegistry _clientRegistry;

    public AgentChatClientFactory(
        IOptions<AgentHostOptions> agentHostOptions,
        IOptions<PurviewDlpOptions> purviewOptions,
        IOptions<InternalMcpOptions> internalMcpOptions,
        AgentIdentityTokenContext tokenContext,
        PurviewAgentIdentityTokenCredential purviewCredential,
        ToolContentProtector toolContentProtector,
        PurviewApplicationLocationResolver purviewApplicationLocation,
        ILoggerFactory loggerFactory,
        IHostEnvironment environment)
    {
        _agentHostOptions = agentHostOptions.Value;
        _purviewOptions = purviewOptions.Value;
        _internalMcpOptions = internalMcpOptions.Value;
        _tokenContext = tokenContext;
        _purviewCredential = purviewCredential;
        _toolContentProtector = toolContentProtector;
        _purviewApplicationLocation = purviewApplicationLocation;
        _loggerFactory = loggerFactory;

        if (environment.IsDevelopment() || environment.IsEnvironment("Playground"))
        {
            _localCredential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ExcludeManagedIdentityCredential = true
            });
        }

        _clientRegistry = new AgentChatClientRegistry(CreateClient);
    }

    public IChatClient Create()
    {
        var applicationId = _purviewOptions.Enabled
            ? _purviewApplicationLocation.Resolve()
            : string.Empty;
        return _clientRegistry.Create(applicationId);
    }

    public void Dispose() => _clientRegistry.Dispose();

    // The Foundry account endpoint is the OpenAI-compatible base; the Responses client appends its
    // own route below /openai/v1, so no request path or api-version belongs in configuration. The
    // project-scoped /api/projects/<name> form does not publish /openai/v1 and must not be used.
    internal static Uri BuildResponsesEndpoint(string foundryProjectEndpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foundryProjectEndpoint);

        return new Uri($"{foundryProjectEndpoint.TrimEnd('/')}/openai/v1");
    }

    private IChatClient CreateClient(string applicationId)
    {
        TokenCredential credential = _tokenContext.Current is not null
            ? new AgentIdentityTokenCredential(_tokenContext)
            : _localCredential
                ?? throw new InvalidOperationException(
                    "An Agent Identity token context is required outside local development.");

        // gpt-5.6-sol is a Responses API model, so the host resolves it through the Foundry
        // project Responses endpoint rather than the legacy chat-completions client. Stored
        // output is disabled so no prompt or response content is retained service-side.
        // The per-turn Agent Identity credential is carried by a bearer token policy.
#pragma warning disable OPENAI001, MAAI001 // Responses client and adapter are evaluation APIs.
        var chatClientBuilder = new OpenAIClient(
                new BearerTokenPolicy(credential, AgentIdentityAuthorizationScopes.Foundry),
                new OpenAIClientOptions
                {
                    Endpoint = BuildResponsesEndpoint(_agentHostOptions.FoundryProjectEndpoint)
                })
            .GetResponsesClient()
            .AsIChatClientWithStoredOutputDisabled(_agentHostOptions.FoundryModelDeployment)
            .AsBuilder();
#pragma warning restore OPENAI001, MAAI001

        if (_purviewOptions.Enabled)
        {
            var purviewCredential = _tokenContext.Current is not null
                ? _purviewCredential
                : credential;
            chatClientBuilder.WithPurview(
                purviewCredential,
                PurviewSettingsFactory.Create(
                    _purviewOptions,
                    applicationId),
                _loggerFactory.CreateLogger("PurviewDlp"));
        }

        // Purview protects the user prompt and final answer. Tool arguments and results are
        // independently protected before function execution by ToolContentProtector.
        chatClientBuilder.UseFunctionInvocation(
            _loggerFactory,
            functionInvocation =>
            {
                functionInvocation.IncludeDetailedErrors = false;
                functionInvocation.MaximumConsecutiveErrorsPerRequest = 0;
                if (_internalMcpOptions.Enabled)
                {
                    functionInvocation.FunctionInvoker = _toolContentProtector.InvokeAsync;
                }
            });

        // Telemetry is innermost and may never capture raw prompts, responses, or tool values.
        chatClientBuilder.UseOpenTelemetry(
            sourceName: null,
            configure: instrumentation => instrumentation.EnableSensitiveData = false);

        return chatClientBuilder.Build();
    }
}

internal sealed class AgentChatClientRegistry : IDisposable
{
    // This exceeds the supported per-replica concurrent-turn envelope, so eviction bounds long-lived
    // revisions without disposing a wrapper that can still belong to an active turn.
    private const int DefaultRetainedClientCapacity = 4096;
    private readonly Func<string, IChatClient> _clientFactory;
    private readonly Queue<IChatClient> _clients = [];
    private readonly object _sync = new();
    private readonly int _retainedClientCapacity;
    private bool _disposed;

    internal AgentChatClientRegistry(
        Func<string, IChatClient> clientFactory,
        int retainedClientCapacity = DefaultRetainedClientCapacity)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedClientCapacity, 1);
        _clientFactory = clientFactory;
        _retainedClientCapacity = retainedClientCapacity;
    }

    public IChatClient Create(string applicationId)
    {
        ArgumentNullException.ThrowIfNull(applicationId);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        var client = _clientFactory(applicationId);
        IChatClient? evictedClient = null;
        var registryDisposed = false;
        lock (_sync)
        {
            if (_disposed)
            {
                registryDisposed = true;
            }
            else
            {
                _clients.Enqueue(client);
                if (_clients.Count > _retainedClientCapacity)
                {
                    evictedClient = _clients.Dequeue();
                }
            }
        }

        if (registryDisposed)
        {
            client.Dispose();
            throw new ObjectDisposedException(nameof(AgentChatClientRegistry));
        }

        evictedClient?.Dispose();
        return client;
    }

    public void Dispose()
    {
        IChatClient[] clients;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            clients = [.. _clients];
            _clients.Clear();
            _disposed = true;
        }

        foreach (var client in clients)
        {
            client.Dispose();
        }
    }
}
