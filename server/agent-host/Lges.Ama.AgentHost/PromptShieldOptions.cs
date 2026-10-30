using System.ComponentModel.DataAnnotations;

namespace Lges.Ama.AgentHost;

/// <summary>
/// AZURE AI CONTENT SAFETY PROMPT SHIELDS configuration for fail-closed prompt-injection defence.
/// </summary>
/// <remarks>
/// Prompt Shields is a standalone text classifier. It never sees the model, the model endpoint, or
/// any model credential, so this guard keeps working unchanged if inference moves off Microsoft
/// Foundry to an external provider. <see cref="Endpoint"/> is deliberately plain configuration so a
/// dedicated <c>ContentSafety</c> account can replace the multi-service account without a code
/// change.
/// </remarks>
public sealed class PromptShieldOptions
{
    public const string SectionName = "PromptShield";

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Content Safety account endpoint, for example
    /// <c>https://&lt;account&gt;.cognitiveservices.azure.com</c>. Required when enabled.
    /// </summary>
    public Uri? Endpoint { get; init; }

    [Required]
    public string ApiVersion { get; init; } = "2024-09-01";

    /// <summary>
    /// Per-request character ceiling. The service rejects oversized segments, so longer content is
    /// split into segments of at most this size and every segment is evaluated.
    /// </summary>
    [Range(1_000, 10_000)]
    public int MaximumSegmentCharacters { get; init; } = 10_000;

    /// <summary>
    /// Upper bound on segments evaluated for a single piece of content. Content that needs more
    /// segments than this is rejected rather than partially evaluated, because skipping a segment
    /// would leave an unevaluated span in front of the model.
    /// </summary>
    [Range(1, 64)]
    public int MaximumSegments { get; init; } = 16;

    [Range(1, 120)]
    public int TimeoutSeconds { get; init; } = 10;

    [Required]
    public string BlockedPromptMessage { get; init; } =
        "Your request was blocked because it looks like an attempt to manipulate the agent's instructions. Error code: STA-SHIELD-001.";

    [Required]
    public string BlockedDocumentMessage { get; init; } =
        "A tool returned content that looks like an embedded instruction attack, so the turn was stopped. Error code: STA-SHIELD-002.";

    [Required]
    public string EvaluationFailureMessage { get; init; } =
        "I could not safely screen this request for prompt-injection attempts. Please try again later. Error code: STA-SHIELD-003.";

    public bool HasValidEndpoint() =>
        !Enabled || (Endpoint is not null && Endpoint.IsAbsoluteUri
            && (Endpoint.Scheme == Uri.UriSchemeHttps
                || (Endpoint.IsLoopback && Endpoint.Scheme == Uri.UriSchemeHttp)));
}
