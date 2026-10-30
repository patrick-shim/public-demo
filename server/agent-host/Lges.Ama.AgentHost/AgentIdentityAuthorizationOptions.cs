using System.ComponentModel.DataAnnotations;

namespace Lges.Ama.AgentHost;

public sealed class AgentIdentityAuthorizationOptions
{
    public const string SectionName = "AgentIdentityAuthorization";

    public AgentIdentityAuthorizationHandlerOptions AgenticUser { get; init; } = new()
    {
        FoundryAuthHandlerName = "agentic-foundry",
        PurviewAuthHandlerName = "agentic-purview",
        InternalMcpAuthHandlerName = "agentic-mcp"
    };

    public AgentIdentityAuthorizationHandlerOptions OnBehalfOf { get; init; } = new()
    {
        FoundryAuthHandlerName = "obo-user",
        PurviewAuthHandlerName = "obo-user",
        InternalMcpAuthHandlerName = "obo-user"
    };

    [Required]
    public string FailureMessage { get; init; } =
        "I could not establish the required Agent Identity for this request. Please try again later.";

    public AgentIdentityAuthorizationHandlerOptions GetHandlers(AgentFrontendMode mode) =>
        mode switch
        {
            AgentFrontendMode.AgenticUser => AgenticUser,
            AgentFrontendMode.OnBehalfOf => OnBehalfOf,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown frontend mode.")
        };
}

public sealed class AgentIdentityAuthorizationHandlerOptions
{
    [Required]
    public string FoundryAuthHandlerName { get; init; } = string.Empty;

    [Required]
    public string PurviewAuthHandlerName { get; init; } = string.Empty;

    [Required]
    public string InternalMcpAuthHandlerName { get; init; } = string.Empty;

    public bool HasRequiredHandlerNames(bool includePurview, bool includeInternalMcp) =>
        !string.IsNullOrWhiteSpace(FoundryAuthHandlerName)
        && (!includePurview || !string.IsNullOrWhiteSpace(PurviewAuthHandlerName))
        && (!includeInternalMcp || !string.IsNullOrWhiteSpace(InternalMcpAuthHandlerName));

    public string[] GetAutoSignInHandlerNames(bool includePurview, bool includeInternalMcp)
    {
        var handlerNames = new List<string> { FoundryAuthHandlerName };
        if (includePurview)
        {
            handlerNames.Add(PurviewAuthHandlerName);
        }

        if (includeInternalMcp)
        {
            handlerNames.Add(InternalMcpAuthHandlerName);
        }

        return [.. handlerNames.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal)];
    }
}
