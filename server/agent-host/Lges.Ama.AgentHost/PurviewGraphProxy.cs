using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Lges.Ama.AgentHost;

public sealed partial class PurviewGraphProxy(
    IHttpClientFactory httpClientFactory,
    IOptions<PurviewDlpOptions> options,
    ILogger<PurviewGraphProxy> logger)
{
    private static readonly byte[] EmptyTextSuccessResponse =
        "{\"protectionScopeState\":\"notModified\",\"policyActions\":[],\"processingErrors\":[]}"u8.ToArray();

    private static readonly byte[] IncompleteEvaluationResponse =
        "{\"error\":{\"code\":\"PurviewEvaluationIncomplete\",\"message\":\"The content protection evaluation did not complete successfully.\"}}"u8.ToArray();

    private static readonly string[] SafeDiagnosticKeywords =
    [
        "already",
        "correlation",
        "duplicate",
        "empty",
        "exists",
        "format",
        "guid",
        "invalid",
        "length",
        "mismatch",
        "missing",
        "required",
        "sequence",
        "unique"
    ];

    private readonly PurviewDlpOptions _options = options.Value;

    public async Task ForwardAsync(
        HttpContext context,
        string? path,
        CancellationToken cancellationToken)
    {
        if (!_options.UseCompatibilityProxy
            || context.Connection.RemoteIpAddress is not { } remoteAddress
            || !IPAddress.IsLoopback(remoteAddress)
            || !TryValidatePath(path, out var normalizedPath, out var operation))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var targetUri = new Uri(_options.GraphBaseUri, normalizedPath);
        if (!string.Equals(targetUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Purview Graph forwarding requires HTTPS.");
        }

        using var requestBuffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(requestBuffer, cancellationToken);
        var requestBody = requestBuffer.ToArray();
        var requestShape = InspectRequest(requestBody);
        if (string.Equals(operation, "processContent", StringComparison.Ordinal)
            && requestShape.HasOnlyEmptyTextContent)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(EmptyTextSuccessResponse, cancellationToken);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, targetUri)
        {
            Content = new ByteArrayContent(requestBody)
        };
        CopyRequestHeader(context, request, "Authorization");
        CopyRequestHeader(context, request, "If-None-Match");
        CopyRequestHeader(context, request, "Client-Request-Id");
        CopyRequestHeader(context, request, "User-Agent");
        // The SDK requests inline evaluation for applicable enforcement scopes.
        // Preserve that preference without forwarding arbitrary client preferences.
        var inlinePreferenceForwarded = string.Equals(operation, "processContent", StringComparison.Ordinal)
            && CopyInlinePreference(context, request);
        if (!request.Headers.Contains("Client-Request-Id"))
        {
            request.Headers.TryAddWithoutValidation(
                "Client-Request-Id",
                Guid.NewGuid().ToString("D"));
        }

        if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
        {
            request.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
        }

        using var response = await httpClientFactory
            .CreateClient("PurviewGraphProxy")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        context.Response.StatusCode = (int)response.StatusCode;
        CopyResponseHeaders(response, context.Response);

        if (!response.IsSuccessStatusCode)
        {
            var error = ClassifyError(responseBody);
            LogPurviewGraphFailure(
                logger,
                operation,
                (int)response.StatusCode,
                error.Code,
                error.Category,
                error.Keywords,
                requestShape.EntryCount,
                requestShape.EmptyCorrelationCount,
                requestShape.InvalidCorrelationCount,
                requestShape.DistinctCorrelationCount,
                requestShape.EmptyContentCount,
                GetResponseHeader(response, "request-id"));
        }
        else if (string.Equals(operation, "processContent", StringComparison.Ordinal))
        {
            var policyRequest = InspectPolicyRequest(requestBody);
            var policyResponse = InspectPolicyResponse(responseBody);
            LogPurviewGraphEvaluation(
                logger,
                policyRequest.Activity,
                policyRequest.ApplicationId,
                (int)response.StatusCode,
                policyResponse.ActionCount,
                policyResponse.BlockCount,
                policyResponse.ErrorCount,
                policyResponse.ScopeState,
                inlinePreferenceForwarded);

            // The installed SDK treats an empty policy-action list as allow, even
            // when Graph reports processing errors or an inline evaluation is pending.
            // Make those evaluation failures explicit without changing valid decisions
            // or the SDK's audit-only/no-inline behavior.
            if (policyResponse.ErrorCount > 0
                || (inlinePreferenceForwarded
                    && (response.StatusCode != HttpStatusCode.OK
                        || policyResponse.ActionCount < 0
                        || policyResponse.ErrorCount < 0)))
            {
                LogPurviewIncompleteEvaluation(logger, (int)response.StatusCode, inlinePreferenceForwarded);
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                context.Response.Headers.Remove("Content-Length");
                context.Response.ContentType = "application/json";
                await context.Response.Body.WriteAsync(IncompleteEvaluationResponse, cancellationToken);
                return;
            }
        }
        else if (string.Equals(operation, "protectionScopes/compute", StringComparison.Ordinal))
        {
            var scopes = InspectScopesResponse(responseBody);
            LogPurviewProtectionScopes(
                logger,
                (int)response.StatusCode,
                scopes.TotalCount,
                scopes.UploadInlineCount,
                scopes.DownloadInlineCount,
                scopes.OfflineCount);
        }

        await context.Response.Body.WriteAsync(responseBody, cancellationToken);
    }

    private static bool CopyInlinePreference(HttpContext context, HttpRequestMessage request)
    {
        if (context.Request.Headers.TryGetValue("Prefer", out var values)
            && values.Any(value => value?.Split(',').Any(preference =>
                string.Equals(preference.Trim(), "evaluateInline", StringComparison.OrdinalIgnoreCase)) == true))
        {
            request.Headers.Add("Prefer", "evaluateInline");
            return true;
        }

        return false;
    }

    private static PurviewPolicyRequest InspectPolicyRequest(ReadOnlySpan<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("contentToProcess", out var content)
                && content.ValueKind == JsonValueKind.Object)
            {
                var activity = content.TryGetProperty("activityMetadata", out var metadata)
                    && metadata.ValueKind == JsonValueKind.Object
                    ? ReadString(metadata, "activity")
                    : null;
                var applicationId = content.TryGetProperty("protectedAppMetadata", out var app)
                    && app.ValueKind == JsonValueKind.Object
                    && app.TryGetProperty("applicationLocation", out var location)
                    && location.ValueKind == JsonValueKind.Object
                    && Guid.TryParse(ReadString(location, "value"), out var parsed)
                        ? parsed.ToString("D")
                        : "unknown";
                return new PurviewPolicyRequest(
                    activity is "uploadText" or "downloadText" or "uploadFile" or "downloadFile"
                        ? activity
                        : "unknown",
                    applicationId);
            }
        }
        catch (JsonException)
        {
            // Diagnostics never change the Graph request or response.
        }

        return new PurviewPolicyRequest("unknown", "unknown");
    }

    private static PurviewPolicyResponse InspectPolicyResponse(ReadOnlySpan<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new PurviewPolicyResponse(-1, -1, -1, "unknown");
            }

            var actionCount = -1;
            var blockCount = -1;
            if (root.TryGetProperty("policyActions", out var actions)
                && actions.ValueKind == JsonValueKind.Array)
            {
                actionCount = actions.GetArrayLength();
                blockCount = actions.EnumerateArray().Count(action =>
                    action.ValueKind == JsonValueKind.Object
                    && (string.Equals(ReadString(action, "action"), "blockAccess", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(ReadString(action, "restrictionAction"), "block", StringComparison.OrdinalIgnoreCase)));
            }

            var errorCount = root.TryGetProperty("processingErrors", out var errors)
                && errors.ValueKind == JsonValueKind.Array
                    ? errors.GetArrayLength()
                    : -1;
            var state = ReadString(root, "protectionScopeState");
            return new PurviewPolicyResponse(
                actionCount,
                blockCount,
                errorCount,
                state is "modified" or "notModified" ? state : "unknown");
        }
        catch (JsonException)
        {
            return new PurviewPolicyResponse(-1, -1, -1, "invalid-json");
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static PurviewScopeCounts InspectScopesResponse(ReadOnlySpan<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("value", out var scopes)
                || scopes.ValueKind != JsonValueKind.Array)
            {
                return new PurviewScopeCounts(-1, -1, -1, -1);
            }

            var uploadInline = 0;
            var downloadInline = 0;
            var offline = 0;
            foreach (var scope in scopes.EnumerateArray())
            {
                if (scope.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var mode = ReadString(scope, "executionMode");
                if (string.Equals(mode, "evaluateOffline", StringComparison.OrdinalIgnoreCase))
                {
                    offline++;
                }
                else if (string.Equals(mode, "evaluateInline", StringComparison.OrdinalIgnoreCase))
                {
                    var activities = (ReadString(scope, "activities") ?? string.Empty)
                        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (activities.Contains("uploadText", StringComparer.OrdinalIgnoreCase))
                    {
                        uploadInline++;
                    }

                    if (activities.Contains("downloadText", StringComparer.OrdinalIgnoreCase))
                    {
                        downloadInline++;
                    }
                }
            }

            return new PurviewScopeCounts(scopes.GetArrayLength(), uploadInline, downloadInline, offline);
        }
        catch (JsonException)
        {
            return new PurviewScopeCounts(-1, -1, -1, -1);
        }
    }

    private static void CopyRequestHeader(
        HttpContext context,
        HttpRequestMessage request,
        string headerName)
    {
        if (context.Request.Headers.TryGetValue(headerName, out var values))
        {
            request.Headers.TryAddWithoutValidation(headerName, values.ToArray());
        }
    }

    private static void CopyResponseHeaders(HttpResponseMessage source, HttpResponse target)
    {
        foreach (var header in source.Headers)
        {
            target.Headers[header.Key] = header.Value.ToArray();
        }

        foreach (var header in source.Content.Headers)
        {
            target.Headers[header.Key] = header.Value.ToArray();
        }

        target.Headers.Remove("transfer-encoding");
    }

    private static string? GetResponseHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? values.FirstOrDefault()
            : null;

    private static bool TryValidatePath(
        string? path,
        out string normalizedPath,
        out string operation)
    {
        normalizedPath = string.Empty;
        operation = string.Empty;
        var segments = path?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (segments.Length is < 4 or > 5
            || !string.Equals(segments[0], "users", StringComparison.Ordinal)
            || !Guid.TryParse(segments[1], out _)
            || !string.Equals(segments[2], "dataSecurityAndGovernance", StringComparison.Ordinal))
        {
            return false;
        }

        operation = string.Join('/', segments[3..]);
        if (operation is not ("processContent"
            or "protectionScopes/compute"
            or "activities/contentActivities"))
        {
            return false;
        }

        normalizedPath = string.Join('/', segments);
        return true;
    }

    private static PurviewGraphError ClassifyError(ReadOnlySpan<byte> responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody.ToArray());
            var error = document.RootElement.GetProperty("error");
            var code = error.TryGetProperty("code", out var codeValue)
                ? codeValue.GetString() ?? "unknown"
                : "unknown";
            var message = error.TryGetProperty("message", out var messageValue)
                ? messageValue.GetString() ?? string.Empty
                : string.Empty;
            return new PurviewGraphError(
                code,
                ClassifyMessage(message),
                string.Join(
                    ',',
                    SafeDiagnosticKeywords.Where(
                        keyword => message.Contains(keyword, StringComparison.OrdinalIgnoreCase))));
        }
        catch (JsonException)
        {
            return new PurviewGraphError("invalid-json", "unclassified", string.Empty);
        }
    }

    private static PurviewRequestShape InspectRequest(ReadOnlySpan<byte> requestBody)
    {
        try
        {
            using var document = JsonDocument.Parse(requestBody.ToArray());
            if (!document.RootElement.TryGetProperty("contentToProcess", out var contentToProcess)
                || !contentToProcess.TryGetProperty("contentEntries", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
            {
                return PurviewRequestShape.Empty;
            }

            var correlations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entryCount = 0;
            var emptyCorrelationCount = 0;
            var invalidCorrelationCount = 0;
            var emptyContentCount = 0;
            var nonTextContentCount = 0;
            foreach (var entry in entries.EnumerateArray())
            {
                entryCount++;
                var correlation = entry.TryGetProperty("correlationId", out var correlationValue)
                    ? correlationValue.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(correlation))
                {
                    emptyCorrelationCount++;
                }
                else
                {
                    correlations.Add(correlation);
                    if (!Guid.TryParse(correlation, out _))
                    {
                        invalidCorrelationCount++;
                    }
                }

                if (!entry.TryGetProperty("content", out var content)
                    || !content.TryGetProperty("@odata.type", out var contentType)
                    || !string.Equals(
                        contentType.GetString(),
                        "microsoft.graph.textContent",
                        StringComparison.Ordinal))
                {
                    nonTextContentCount++;
                }
                else if (!content.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(data.GetString()))
                {
                    emptyContentCount++;
                }
            }

            return new PurviewRequestShape(
                entryCount,
                emptyCorrelationCount,
                invalidCorrelationCount,
                correlations.Count,
                emptyContentCount,
                nonTextContentCount);
        }
        catch (JsonException)
        {
            return PurviewRequestShape.Empty;
        }
    }

    private static string ClassifyMessage(string message)
    {
        if (message.Contains("correlation", StringComparison.OrdinalIgnoreCase))
        {
            if (message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
                || message.Contains("already", StringComparison.OrdinalIgnoreCase)
                || message.Contains("exists", StringComparison.OrdinalIgnoreCase))
            {
                return "correlation-duplicate";
            }

            if (message.Contains("sequence", StringComparison.OrdinalIgnoreCase))
            {
                return "correlation-sequence";
            }

            if (message.Contains("format", StringComparison.OrdinalIgnoreCase)
                || message.Contains("guid", StringComparison.OrdinalIgnoreCase)
                || message.Contains("invalid", StringComparison.OrdinalIgnoreCase))
            {
                return "correlation-format";
            }

            if (message.Contains("missing", StringComparison.OrdinalIgnoreCase)
                || message.Contains("required", StringComparison.OrdinalIgnoreCase)
                || message.Contains("empty", StringComparison.OrdinalIgnoreCase))
            {
                return "correlation-missing";
            }

            return "correlation";
        }

        if (message.Contains("agent", StringComparison.OrdinalIgnoreCase)
            || message.Contains("blueprint", StringComparison.OrdinalIgnoreCase))
        {
            return "agent-metadata";
        }

        if (message.Contains("application", StringComparison.OrdinalIgnoreCase)
            || message.Contains("location", StringComparison.OrdinalIgnoreCase))
        {
            return "application-location";
        }

        if (message.Contains("policy", StringComparison.OrdinalIgnoreCase))
        {
            return "policy";
        }

        if (message.Contains("payment", StringComparison.OrdinalIgnoreCase)
            || message.Contains("billing", StringComparison.OrdinalIgnoreCase))
        {
            return "billing";
        }

        if (message.Contains("activity", StringComparison.OrdinalIgnoreCase))
        {
            return "activity";
        }

        return "unclassified";
    }

    private sealed record PurviewGraphError(string Code, string Category, string Keywords);

    private sealed record PurviewPolicyRequest(string Activity, string ApplicationId);

    private sealed record PurviewPolicyResponse(int ActionCount, int BlockCount, int ErrorCount, string ScopeState);

    private sealed record PurviewScopeCounts(int TotalCount, int UploadInlineCount, int DownloadInlineCount, int OfflineCount);

    private sealed record PurviewRequestShape(
        int EntryCount,
        int EmptyCorrelationCount,
        int InvalidCorrelationCount,
        int DistinctCorrelationCount,
        int EmptyContentCount,
        int NonTextContentCount)
    {
        public bool HasOnlyEmptyTextContent =>
            EntryCount > 0
            && EmptyContentCount == EntryCount
            && NonTextContentCount == 0;

        public static PurviewRequestShape Empty { get; } = new(0, 0, 0, 0, 0, 0);
    }

    [LoggerMessage(
        EventId = 1020,
        Level = LogLevel.Warning,
        Message = "Purview Graph {Operation} failed with status {StatusCode}, code {ErrorCode}, category {Category}, keywords {Keywords}, entries {EntryCount}, empty correlations {EmptyCorrelationCount}, invalid correlations {InvalidCorrelationCount}, distinct correlations {DistinctCorrelationCount}, empty contents {EmptyContentCount}, request {RequestId}.")]
    private static partial void LogPurviewGraphFailure(
        ILogger logger,
        string operation,
        int statusCode,
        string errorCode,
        string category,
        string keywords,
        int entryCount,
        int emptyCorrelationCount,
        int invalidCorrelationCount,
        int distinctCorrelationCount,
        int emptyContentCount,
        string? requestId);

    [LoggerMessage(
        EventId = 1021,
        Level = LogLevel.Information,
        Message = "Purview Graph processContent completed. activity={Activity}, applicationLocation={ApplicationId}, status={StatusCode}, policyActions={ActionCount}, blockActions={BlockCount}, processingErrors={ErrorCount}, protectionScopeState={ScopeState}, preferInlineForwarded={InlinePreferenceForwarded}.")]
    private static partial void LogPurviewGraphEvaluation(
        ILogger logger,
        string activity,
        string applicationId,
        int statusCode,
        int actionCount,
        int blockCount,
        int errorCount,
        string scopeState,
        bool inlinePreferenceForwarded);

    [LoggerMessage(
        EventId = 1022,
        Level = LogLevel.Information,
        Message = "Purview Graph protectionScopes completed. status={StatusCode}, totalScopeCount={TotalCount}, uploadInlineCount={UploadInlineCount}, downloadInlineCount={DownloadInlineCount}, offlineCount={OfflineCount}.")]
    private static partial void LogPurviewProtectionScopes(
        ILogger logger,
        int statusCode,
        int totalCount,
        int uploadInlineCount,
        int downloadInlineCount,
        int offlineCount);

    [LoggerMessage(
        EventId = 1023,
        Level = LogLevel.Warning,
        Message = "Purview evaluation failed closed because the result was incomplete. upstreamStatus={StatusCode}, preferInlineForwarded={InlinePreferenceForwarded}.")]
    private static partial void LogPurviewIncompleteEvaluation(
        ILogger logger,
        int statusCode,
        bool inlinePreferenceForwarded);
}
