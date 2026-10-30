using System.Collections.ObjectModel;
using Azure.Core;
using Microsoft.Extensions.Options;

namespace Lges.Ama.AgentHost;

public static class AgentIdentityAuthorizationScopes
{
    public const string Foundry = "https://ai.azure.com/.default";
    // Azure AI Content Safety, used by the Prompt Shields guard. This is a different resource from
    // the Foundry inference audience above, and is requested separately so the guard keeps working
    // if inference moves to a provider outside Azure.
    public const string ContentSafety = "https://cognitiveservices.azure.com/.default";
    public const string Purview = "https://graph.microsoft.com/Content.Process.User";
    public const string PurviewProtectionScopes =
        "https://graph.microsoft.com/ProtectionScopes.Compute.User";
    public const string PurviewContentActivity =
        "https://graph.microsoft.com/ContentActivity.Write";
    public const string GraphDefault = "https://graph.microsoft.com/.default";
    public const string ObservabilityDefault =
        "api://9b975845-388f-4429-889e-eab1ef63949c/.default";

    public static string InternalMcp(string audience) =>
        $"{audience.TrimEnd('/')}/Mcp.Invoke";

    public static string InternalMcpDefault(string audience) =>
        $"{audience.TrimEnd('/')}/.default";

    public static readonly string[] PurviewDelegated =
    [
        Purview,
        PurviewProtectionScopes,
        PurviewContentActivity
    ];
}

public sealed class AgentIdentityTurnTokens
{
    private readonly ReadOnlyDictionary<string, string> _accessTokens;

    public AgentIdentityTurnTokens(
        string agentId,
        string tenantId,
        IEnumerable<KeyValuePair<string, string>> accessTokens)
        : this(agentId, tenantId, userId: null, accessTokens)
    {
    }

    public AgentIdentityTurnTokens(
        string agentId,
        string tenantId,
        Guid? userId,
        IEnumerable<KeyValuePair<string, string>> accessTokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(accessTokens);

        AgentId = agentId;
        TenantId = tenantId;
        UserId = userId;
        _accessTokens = new ReadOnlyDictionary<string, string>(
            accessTokens.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal));
    }

    public string AgentId { get; }

    public string TenantId { get; }

    public Guid? UserId { get; }

    public string RequireAccessToken(IEnumerable<string> requestedScopes)
    {
        ArgumentNullException.ThrowIfNull(requestedScopes);

        string? resolvedToken = null;
        foreach (var scope in requestedScopes)
        {
            if (!_accessTokens.TryGetValue(scope, out var token)
                || string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    $"The current Agent Identity turn has no token for requested scope '{scope}'.");
            }

            if (resolvedToken is not null
                && !string.Equals(resolvedToken, token, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A single credential request cannot span multiple Agent Identity resources.");
            }

            resolvedToken = token;
        }

        return resolvedToken
            ?? throw new InvalidOperationException("At least one token scope must be requested.");
    }
}

public sealed class AgentIdentityTokenContext
{
    private readonly AsyncLocal<AgentIdentityTurnTokens?> _current = new();

    public AgentIdentityTurnTokens? Current => _current.Value;

    public IDisposable Push(AgentIdentityTurnTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var prior = _current.Value;
        _current.Value = tokens;
        return new TokenScope(this, tokens, prior);
    }

    public AgentIdentityTurnTokens RequireCurrent() =>
        Current ?? throw new InvalidOperationException(
            "No Agent Identity token context is active for the current turn.");

    private sealed class TokenScope(
        AgentIdentityTokenContext owner,
        AgentIdentityTurnTokens current,
        AgentIdentityTurnTokens? prior) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (!ReferenceEquals(owner._current.Value, current))
            {
                throw new InvalidOperationException(
                    "Agent Identity token scopes must be disposed in creation order.");
            }

            owner._current.Value = prior;
            _disposed = true;
        }
    }
}

public sealed class AgentIdentityTokenCredential(AgentIdentityTokenContext tokenContext)
    : TokenCredential
{
    public override AccessToken GetToken(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken) =>
        CreateAccessToken(requestContext);

    public override ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(CreateAccessToken(requestContext));

    private AccessToken CreateAccessToken(TokenRequestContext requestContext) =>
        new(
            tokenContext.RequireCurrent().RequireAccessToken(requestContext.Scopes),
            DateTimeOffset.UtcNow.AddMinutes(5));
}

public sealed class PurviewAgentIdentityTokenCredential(
    AgentIdentityTokenContext tokenContext,
    IOptions<PurviewDlpOptions> options) : TokenCredential
{
    private readonly PurviewDlpOptions _options = options.Value;

    public override AccessToken GetToken(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken) =>
        CreateAccessToken(requestContext);

    public override ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(CreateAccessToken(requestContext));

    private AccessToken CreateAccessToken(TokenRequestContext requestContext)
    {
        var scopes = requestContext.Scopes;
        var expectedProxyScope = $"https://{_options.CompatibilityProxyBaseUri.Host}/.default";
        if (_options.UseCompatibilityProxy
            && scopes.Length == 1
            && string.Equals(scopes[0], expectedProxyScope, StringComparison.OrdinalIgnoreCase))
        {
            scopes = [AgentIdentityAuthorizationScopes.GraphDefault];
        }

        return new AccessToken(
            tokenContext.RequireCurrent().RequireAccessToken(scopes),
            DateTimeOffset.UtcNow.AddMinutes(5));
    }
}
