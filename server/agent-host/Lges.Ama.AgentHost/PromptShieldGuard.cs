using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Lges.Ama.AgentHost;

/// <summary>
/// Attaches the per-turn child Agent Identity token for the Content Safety resource.
/// </summary>
/// <remarks>
/// Prompt Shields is called with an Entra token, never an account key, so no Content Safety
/// credential exists in configuration or in the container environment.
/// </remarks>
public sealed class PromptShieldBearerTokenHandler(AgentIdentityTokenContext tokenContext)
    : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = tokenContext
            .RequireCurrent()
            .RequireAccessToken([AgentIdentityAuthorizationScopes.ContentSafety]);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return base.SendAsync(request, cancellationToken);
    }
}

public enum PromptShieldSurface
{
    /// <summary>Text the signed-in user sent. Detects direct jailbreak attempts.</summary>
    UserPrompt,

    /// <summary>Grounding content returned by a tool. Detects indirect prompt injection.</summary>
    Document
}

/// <summary>
/// Fail-closed Prompt Shields guard.
/// </summary>
/// <remarks>
/// Two distinct surfaces are screened. <see cref="PromptShieldSurface.UserPrompt"/> covers the
/// message the user typed. <see cref="PromptShieldSurface.Document"/> covers text a tool returned,
/// which Microsoft Purview chat middleware does not inspect: Purview protects prompts and model
/// responses, not tool arguments and results, so without this guard a malicious string embedded in
/// third-party data would reach the model unscreened.
///
/// Every failure path throws. A transport error, a non-success status, an unparsable body, or
/// content that needs more segments than allowed all reject the turn rather than letting
/// unevaluated text through.
/// </remarks>
public sealed partial class PromptShieldGuard(
    HttpClient httpClient,
    IOptions<PromptShieldOptions> options,
    ILogger<PromptShieldGuard> logger)
{
    public const string HttpClientName = "PromptShield";

    private readonly PromptShieldOptions _options = options.Value;

    public bool Enabled => _options.Enabled;

    public async Task EvaluateAsync(
        string content,
        PromptShieldSurface surface,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var segments = Segment(content, surface);
        foreach (var segment in segments)
        {
            await EvaluateSegmentAsync(segment, surface, cancellationToken);
        }
    }

    private List<string> Segment(string content, PromptShieldSurface surface)
    {
        var size = _options.MaximumSegmentCharacters;
        if (content.Length <= size)
        {
            return [content];
        }

        var required = (content.Length + size - 1) / size;
        if (required > _options.MaximumSegments)
        {
            // Refusing is the safe branch: evaluating only a prefix would leave the remainder
            // unscreened while still reaching the model.
            throw new PromptShieldEvaluationException(
                $"Content requires {required} Prompt Shields segments, above the configured maximum.");
        }

        var segments = new List<string>(required);
        for (var offset = 0; offset < content.Length; offset += size)
        {
            segments.Add(content.Substring(offset, Math.Min(size, content.Length - offset)));
        }

        LogSegmented(logger, surface, segments.Count);
        return segments;
    }

    private async Task EvaluateSegmentAsync(
        string segment,
        PromptShieldSurface surface,
        CancellationToken cancellationToken)
    {
        var request = surface == PromptShieldSurface.UserPrompt
            ? new ShieldPromptRequest { UserPrompt = segment, Documents = [] }
            : new ShieldPromptRequest { UserPrompt = string.Empty, Documents = [segment] };

        ShieldPromptResponse? result;
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"contentsafety/text:shieldPrompt?api-version={_options.ApiVersion}",
                request,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // The body can echo the submitted content, so only the status is recorded.
                throw new PromptShieldEvaluationException(
                    $"Prompt Shields returned HTTP {(int)response.StatusCode}.");
            }

            result = await response.Content.ReadFromJsonAsync<ShieldPromptResponse>(
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is not PromptShieldEvaluationException
            && !RequestCancellation.IsRequested(exception, cancellationToken))
        {
            throw new PromptShieldEvaluationException(
                "Prompt Shields could not be reached.",
                exception);
        }

        if (result is null)
        {
            throw new PromptShieldEvaluationException("Prompt Shields returned no analysis.");
        }

        var detected = surface == PromptShieldSurface.UserPrompt
            ? result.UserPromptAnalysis?.AttackDetected == true
            : result.DocumentsAnalysis?.Any(analysis => analysis.AttackDetected) == true;

        if (detected)
        {
            LogAttackDetected(logger, surface);
            throw new PromptShieldBlockedException(surface);
        }
    }

    [LoggerMessage(
        EventId = 1300,
        Level = LogLevel.Warning,
        Message = "Prompt Shields blocked a turn. surface={Surface}.")]
    private static partial void LogAttackDetected(ILogger logger, PromptShieldSurface surface);

    [LoggerMessage(
        EventId = 1301,
        Level = LogLevel.Debug,
        Message = "Prompt Shields split content into {SegmentCount} segments. surface={Surface}.")]
    private static partial void LogSegmented(
        ILogger logger,
        PromptShieldSurface surface,
        int segmentCount);

    private sealed class ShieldPromptRequest
    {
        [JsonPropertyName("userPrompt")]
        public string UserPrompt { get; init; } = string.Empty;

        [JsonPropertyName("documents")]
        public string[] Documents { get; init; } = [];
    }

    private sealed class ShieldPromptResponse
    {
        [JsonPropertyName("userPromptAnalysis")]
        public AnalysisResult? UserPromptAnalysis { get; init; }

        [JsonPropertyName("documentsAnalysis")]
        public AnalysisResult[]? DocumentsAnalysis { get; init; }
    }

    private sealed class AnalysisResult
    {
        [JsonPropertyName("attackDetected")]
        public bool AttackDetected { get; init; }
    }
}

public sealed class PromptShieldBlockedException(PromptShieldSurface surface)
    : Exception($"Prompt Shields blocked {surface.ToString().ToLowerInvariant()} content.")
{
    public PromptShieldSurface Surface { get; } = surface;
}

public sealed class PromptShieldEvaluationException : Exception
{
    public PromptShieldEvaluationException(string message)
        : base(message)
    {
    }

    public PromptShieldEvaluationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
