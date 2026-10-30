using System.IdentityModel.Tokens.Jwt;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Options;

namespace Lges.Ama.AgentHost;

public sealed record AgentFrontendIdentity(string AgentId, string TenantId);

public sealed class AgentFrontendIdentityBinding(
    IOptions<AgentTokenValidationOptions> tokenValidationOptions,
    IOptions<AgentIdentityOboOptions> oboOptions)
{
    private readonly AgentTokenValidationOptions _tokenValidationOptions = tokenValidationOptions.Value;
    private readonly AgentIdentityOboOptions _oboOptions = oboOptions.Value;

    public AgentFrontendIdentity Resolve(IActivity activity, AgentFrontendMode mode)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var expectedTenantId = RequireGuid(
            _tokenValidationOptions.TenantId,
            "TokenValidation:TenantId");
        var activityTenantId = activity.GetAgenticTenantId();
        var mayUseConfiguredOboTenant = mode == AgentFrontendMode.OnBehalfOf
            && string.IsNullOrWhiteSpace(activityTenantId);
        if (!mayUseConfiguredOboTenant
            && !string.Equals(activityTenantId, expectedTenantId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The activity tenant does not match the authenticated frontend tenant.");
        }

        var agentId = mode switch
        {
            AgentFrontendMode.AgenticUser => activity.GetAgenticInstanceId(),
            AgentFrontendMode.OnBehalfOf => _oboOptions.AgentId,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported frontend mode.")
        };
        return new AgentFrontendIdentity(
            RequireGuid(agentId, $"{mode} child Agent Identity"),
            expectedTenantId);
    }

    public static Guid ValidateUserAssertion(
        string token,
        string expectedTenantId,
        string? activityUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedTenantId);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var tokenTenantId = jwt.Claims.FirstOrDefault(claim => claim.Type == "tid")?.Value;
        if (!string.Equals(tokenTenantId, expectedTenantId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The OBO user assertion tenant does not match the authenticated frontend tenant.");
        }

        var tokenUserId = jwt.Claims.FirstOrDefault(claim => claim.Type == "oid")?.Value;
        if (!Guid.TryParse(tokenUserId, out var resolvedUserId))
        {
            throw new InvalidOperationException(
                "The OBO user assertion does not identify a human Entra user.");
        }

        if (!string.IsNullOrWhiteSpace(activityUserId)
            && (!Guid.TryParse(activityUserId, out var resolvedActivityUserId)
                || resolvedActivityUserId != resolvedUserId))
        {
            throw new InvalidOperationException(
                "The activity user does not match the OBO user assertion.");
        }

        return resolvedUserId;
    }

    public static void ValidateAgentToken(string token, string expectedAgentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedAgentId);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var tokenAgentId = jwt.Claims
            .FirstOrDefault(claim => claim.Type is "appid" or "azp")
            ?.Value;
        if (!string.Equals(tokenAgentId, expectedAgentId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The acquired resource token does not belong to the activity child Agent Identity.");
        }
    }

    private static string RequireGuid(string? value, string settingName) =>
        Guid.TryParse(value, out _)
            ? value
            : throw new InvalidOperationException($"{settingName} must be a valid application or tenant ID.");
}
