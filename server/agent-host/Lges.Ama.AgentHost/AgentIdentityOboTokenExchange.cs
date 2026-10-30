using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.Agents.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace Lges.Ama.AgentHost;

public sealed class AgentIdentityOboOptions
{
    public const string SectionName = "AgentIdentityObo";

    public string AgentId { get; init; } = string.Empty;

    [Required]
    public string BlueprintConnectionName { get; init; } = "OboServiceConnection";
}

public interface IAgentIdentityParentTokenProvider
{
    Task<string> GetParentTokenAsync(
        string tenantId,
        string agentIdentityId,
        CancellationToken cancellationToken);
}

public sealed class AgentIdentityParentTokenProvider(
    IConnections connections,
    IOptions<AgentIdentityOboOptions> options) : IAgentIdentityParentTokenProvider
{
    private readonly AgentIdentityOboOptions _options = options.Value;

    public async Task<string> GetParentTokenAsync(
        string tenantId,
        string agentIdentityId,
        CancellationToken cancellationToken)
    {
        var connection = connections.GetConnection(_options.BlueprintConnectionName);
        if (connection is not IAgenticTokenProvider tokenProvider)
        {
            throw new InvalidOperationException(
                $"Connection '{_options.BlueprintConnectionName}' does not support Agent Identity federation.");
        }

        return await tokenProvider.GetAgenticApplicationTokenAsync(
            tenantId,
            agentIdentityId,
            cancellationToken);
    }
}

public interface IAgentIdentityChildTokenClient
{
    Task<string> AcquireTokenAsync(
        string tenantId,
        string agentIdentityId,
        string parentToken,
        string userAssertion,
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken);

    Task<string> AcquireAppTokenAsync(
        string tenantId,
        string agentIdentityId,
        string parentToken,
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken);
}

public sealed class MsalAgentIdentityChildTokenClient : IAgentIdentityChildTokenClient
{
    public async Task<string> AcquireTokenAsync(
        string tenantId,
        string agentIdentityId,
        string parentToken,
        string userAssertion,
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken)
    {
        var childApplication = CreateChildApplication(tenantId, agentIdentityId, parentToken);

        var result = await childApplication
            .AcquireTokenOnBehalfOf(scopes, new UserAssertion(userAssertion))
            .ExecuteAsync(cancellationToken);
        return result.AccessToken;
    }

    public async Task<string> AcquireAppTokenAsync(
        string tenantId,
        string agentIdentityId,
        string parentToken,
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken)
    {
        var childApplication = CreateChildApplication(tenantId, agentIdentityId, parentToken);
        var result = await childApplication
            .AcquireTokenForClient(scopes)
            .ExecuteAsync(cancellationToken);
        return result.AccessToken;
    }

    private static IConfidentialClientApplication CreateChildApplication(
        string tenantId,
        string agentIdentityId,
        string parentToken) =>
        ConfidentialClientApplicationBuilder
            .Create(agentIdentityId)
            .WithClientAssertion((AssertionRequestOptions _) => Task.FromResult(parentToken))
            .WithAuthority(new Uri($"https://login.microsoftonline.com/{tenantId}"))
            .WithLegacyCacheCompatibility(false)
            .WithCacheOptions(new CacheOptions(useSharedCache: true))
            .Build();
}

public interface IAgentIdentityOboTokenExchange
{
    Task<string> ExchangeAsync(
        string tenantId,
        string agentIdentityId,
        string userAssertion,
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken);

    Task<string> AcquireAppTokenAsync(
        string tenantId,
        string agentIdentityId,
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken);
}

public sealed class AgentIdentityOboTokenExchange(
    IAgentIdentityParentTokenProvider parentTokenProvider,
    IAgentIdentityChildTokenClient childTokenClient) : IAgentIdentityOboTokenExchange
{
    public async Task<string> ExchangeAsync(
        string tenantId,
        string agentIdentityId,
        string userAssertion,
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userAssertion);
        ValidateRequest(tenantId, agentIdentityId, scopes);

        var parentToken = await parentTokenProvider.GetParentTokenAsync(
            tenantId,
            agentIdentityId,
            cancellationToken);
        var childToken = await childTokenClient.AcquireTokenAsync(
            tenantId,
            agentIdentityId,
            parentToken,
            userAssertion,
            scopes,
            cancellationToken);

        ValidateChildToken(childToken, agentIdentityId);
        return childToken;
    }

    public async Task<string> AcquireAppTokenAsync(
        string tenantId,
        string agentIdentityId,
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken)
    {
        ValidateRequest(tenantId, agentIdentityId, scopes);
        var parentToken = await parentTokenProvider.GetParentTokenAsync(
            tenantId,
            agentIdentityId,
            cancellationToken);
        var childToken = await childTokenClient.AcquireAppTokenAsync(
            tenantId,
            agentIdentityId,
            parentToken,
            scopes,
            cancellationToken);

        ValidateChildToken(childToken, agentIdentityId);
        return childToken;
    }

    private static void ValidateRequest(
        string tenantId,
        string agentIdentityId,
        IReadOnlyCollection<string> scopes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentIdentityId);
        ArgumentNullException.ThrowIfNull(scopes);

        if (!Guid.TryParse(tenantId, out _) || !Guid.TryParse(agentIdentityId, out _))
        {
            throw new InvalidOperationException("Agent Identity token acquisition requires valid tenant and child identity IDs.");
        }

        if (scopes.Count == 0
            || scopes.Any(scope => !scope.EndsWith("/.default", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Agent Identity token acquisition requires resource /.default scopes.");
        }
    }

    private static void ValidateChildToken(string childToken, string agentIdentityId)
    {
        var tokenAgentId = ResolveClientId(childToken);
        if (!string.Equals(tokenAgentId, agentIdentityId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Token acquisition did not return a token for the configured child Agent Identity.");
        }
    }

    private static string? ResolveClientId(string token)
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        return jwt.Claims.FirstOrDefault(claim => claim.Type is "appid" or "azp")?.Value;
    }
}
