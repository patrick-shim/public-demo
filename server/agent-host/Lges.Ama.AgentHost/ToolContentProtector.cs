using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure.Core;
using Microsoft.Agents.AI.Purview;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Lges.Ama.AgentHost;

public enum ToolContentDirection
{
    Arguments,
    Result
}

public interface IToolContentEvaluator
{
    ValueTask EvaluateAsync(
        string content,
        Guid userId,
        ToolContentDirection direction,
        CancellationToken cancellationToken);
}

public sealed class ToolContentProtector(
    IToolContentEvaluator evaluator,
    ToolUserContext userContext,
    IOptions<InternalMcpOptions> options)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly InternalMcpOptions _options = options.Value;

    public async ValueTask<object?> InvokeAsync(
        FunctionInvocationContext context,
        CancellationToken cancellationToken)
    {
        var userId = userContext.RequireUserId();
        var arguments = SerializeAndValidate(context.Arguments, ToolContentDirection.Arguments);
        await evaluator.EvaluateAsync(
            arguments,
            userId,
            ToolContentDirection.Arguments,
            cancellationToken);

        var result = await context.Function.InvokeAsync(context.Arguments, cancellationToken);
        var serializedResult = SerializeAndValidate(result, ToolContentDirection.Result);
        await evaluator.EvaluateAsync(
            serializedResult,
            userId,
            ToolContentDirection.Result,
            cancellationToken);
        return result;
    }

    private string SerializeAndValidate(object? value, ToolContentDirection direction)
    {
        string content;
        try
        {
            content = JsonSerializer.Serialize(value, SerializerOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new ToolContentEvaluationException(
                $"The protected tool {direction.ToString().ToLowerInvariant()} could not be serialized.",
                exception);
        }

        if (content.Length > _options.MaximumContentCharacters)
        {
            throw new ToolContentEvaluationException(
                $"The protected tool {direction.ToString().ToLowerInvariant()} exceeded the configured content limit.");
        }

        return content;
    }
}

public sealed class PurviewToolContentEvaluator : IToolContentEvaluator, IDisposable
{
    private readonly PurviewAgentIdentityTokenCredential _credential;
    private readonly PurviewDlpOptions _options;
    private readonly ILogger _logger;
    private readonly PurviewApplicationLocationResolver _purviewApplicationLocation;
    private readonly Dictionary<string, IChatClient> _evaluationClients =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _evaluationClientsLock = new();

    public PurviewToolContentEvaluator(
        PurviewAgentIdentityTokenCredential credential,
        IOptions<PurviewDlpOptions> options,
        PurviewApplicationLocationResolver purviewApplicationLocation,
        ILoggerFactory loggerFactory)
    {
        _credential = credential;
        _options = options.Value;
        _purviewApplicationLocation = purviewApplicationLocation;
        _logger = loggerFactory.CreateLogger("PurviewToolContent");
    }

    public async ValueTask EvaluateAsync(
        string content,
        Guid userId,
        ToolContentDirection direction,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException(
                "Purview DLP must be enabled before internal MCP tools can be invoked.");
        }

        var evaluationClient = GetEvaluationClient();
        var message = new ChatMessage(ChatRole.User, content);
        message.SetUserId(userId);
        var response = await evaluationClient.GetResponseAsync(
            [message],
            cancellationToken: cancellationToken);

        if (response.Messages.Any(message => message.Role == ChatRole.System)
            || string.Equals(response.Text, _options.BlockedPromptMessage, StringComparison.Ordinal)
            || string.Equals(response.Text, _options.BlockedResponseMessage, StringComparison.Ordinal))
        {
            throw new ToolContentBlockedException(direction);
        }
    }

    public void Dispose()
    {
        lock (_evaluationClientsLock)
        {
            foreach (var client in _evaluationClients.Values)
            {
                client.Dispose();
            }

            _evaluationClients.Clear();
        }
    }

    private IChatClient GetEvaluationClient()
    {
        var applicationId = _purviewApplicationLocation.Resolve();
        lock (_evaluationClientsLock)
        {
            if (_evaluationClients.TryGetValue(applicationId, out var client))
            {
                return client;
            }

            client = new EchoChatClient()
                .AsBuilder()
                .WithPurview(
                    _credential,
                    PurviewSettingsFactory.Create(_options, applicationId),
                    _logger)
                .Build();
            _evaluationClients.Add(applicationId, client);
            return client;
        }
    }

    private sealed class EchoChatClient : IChatClient
    {
        public void Dispose()
        {
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var text = messages.LastOrDefault()?.Text ?? string.Empty;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                messages.LastOrDefault()?.Text ?? string.Empty);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;
    }
}

public sealed class ToolContentBlockedException(ToolContentDirection direction)
    : Exception($"Purview blocked protected tool {direction.ToString().ToLowerInvariant()}.")
{
    public ToolContentDirection Direction { get; } = direction;
}

public sealed class ToolContentEvaluationException : Exception
{
    public ToolContentEvaluationException(string message)
        : base(message)
    {
    }

    public ToolContentEvaluationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
