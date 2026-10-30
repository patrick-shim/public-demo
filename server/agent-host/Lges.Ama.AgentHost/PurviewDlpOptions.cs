using System.ComponentModel.DataAnnotations;

namespace Lges.Ama.AgentHost;

/// <summary>
/// PURVIEW DLP configuration for fail-closed prompt and model-response evaluation.
/// </summary>
public sealed class PurviewDlpOptions
{
    public const string SectionName = "PurviewDlp";

    public bool Enabled { get; init; } = true;

    [Required]
    public string AppName { get; init; } = "LG Energy Solution AMA";

    [Required]
    public string AppVersion { get; init; } = "1.0";

    public string? TenantId { get; init; }

    public string ApplicationId { get; init; } = string.Empty;

    [Required]
    public Uri GraphBaseUri { get; init; } = new("https://graph.microsoft.com/v1.0/");

    public bool UseCompatibilityProxy { get; init; }

    public Uri CompatibilityProxyBaseUri { get; init; } =
        new("http://127.0.0.1:8080/internal/purview/");

    [Required]
    public string BlockedPromptMessage { get; init; } =
        "Your request was blocked by your organization's data protection policy.";

    [Required]
    public string BlockedResponseMessage { get; init; } =
        "The response was blocked by your organization's data protection policy.";

    [Required]
    public string EvaluationFailureMessage { get; init; } =
        "I could not safely evaluate this request against the data protection policy. Please try again later.";
}
