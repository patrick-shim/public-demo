using Microsoft.Agents.AI.Purview;
using Microsoft.Extensions.Options;

namespace Lges.Ama.AgentHost;

public static class PurviewSettingsFactory
{
    public static PurviewSettings Create(
        PurviewDlpOptions options,
        string? applicationId = null) => new(options.AppName)
        {
            AppVersion = options.AppVersion,
            TenantId = options.TenantId,
            PurviewAppLocation = new PurviewAppLocation(
            PurviewLocationType.Application,
            applicationId ?? options.ApplicationId),
            IgnoreExceptions = false,
            GraphBaseUri = options.UseCompatibilityProxy
            ? options.CompatibilityProxyBaseUri
            : options.GraphBaseUri,
            BlockedPromptMessage = options.BlockedPromptMessage,
            BlockedResponseMessage = options.BlockedResponseMessage
        };
}

public sealed class PurviewApplicationLocationResolver(
    AgentIdentityTokenContext tokenContext,
    IOptions<AgentIdentityOboOptions> oboOptions,
    IOptions<AgentTokenValidationOptions> tokenValidationOptions,
    IOptions<PurviewDlpOptions> purviewOptions)
{
    private readonly AgentIdentityOboOptions _oboOptions = oboOptions.Value;
    private readonly AgentTokenValidationOptions _tokenValidationOptions = tokenValidationOptions.Value;
    private readonly PurviewDlpOptions _purviewOptions = purviewOptions.Value;

    public string Resolve()
    {
        // The policy's application location can be the blueprint, independently of
        // the channel app used to authenticate incoming OBO messages.
        if (!string.IsNullOrWhiteSpace(_purviewOptions.ApplicationId))
        {
            return _purviewOptions.ApplicationId;
        }

        var agentId = tokenContext.Current?.AgentId;
        if (agentId is null)
        {
            return _purviewOptions.ApplicationId;
        }

        var frontendMode = string.Equals(
            agentId,
            _oboOptions.AgentId,
            StringComparison.OrdinalIgnoreCase)
                ? AgentFrontendMode.OnBehalfOf
                : AgentFrontendMode.AgenticUser;
        return _tokenValidationOptions.GetAudience(frontendMode);
    }
}
