using System.Diagnostics;

namespace Lges.Ama.AgentHost;

/// <summary>Observes only the OBO route before authentication, without inspecting request content.</summary>
public sealed partial class AgentIngressDiagnosticsMiddleware(
    RequestDelegate next,
    ILogger<AgentIngressDiagnosticsMiddleware> logger)
{
    // One observation only; this does not cancel or change the request's timeout.
    internal TimeSpan SlowRequestThreshold { get; init; } = TimeSpan.FromSeconds(30);

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsObservedPath(context.Request.Path))
        {
            await next(context);
            return;
        }

        var requestHash = AgentTurnDiagnostics.HashIdentifier(context.TraceIdentifier);
        var timer = Stopwatch.StartNew();
        LogBegin(logger, SafeMethod(context.Request.Method), requestHash);
        using var completed = new CancellationTokenSource();
        var slowObservation = ObserveSlowRequestAsync(requestHash, timer, completed.Token);
        var failed = false;
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            failed = true;
            LogFailure(logger, requestHash, exception.GetType().Name);
            throw;
        }
        finally
        {
            await completed.CancelAsync();
            await slowObservation;
            LogEnd(logger, requestHash,
                failed && !context.Response.HasStarted ? StatusCodes.Status500InternalServerError : context.Response.StatusCode,
                timer.ElapsedMilliseconds, context.RequestAborted.IsCancellationRequested);
        }
    }

    private async Task ObserveSlowRequestAsync(string requestHash, Stopwatch timer, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SlowRequestThreshold, cancellationToken);
            LogSlow(logger, requestHash, timer.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A completed request needs no slow-request observation.
        }
    }

    internal static bool IsObservedPath(PathString path) =>
        path.Equals(new PathString("/api/messages/obo"), StringComparison.OrdinalIgnoreCase)
        || path.Equals(new PathString("/api/messages/obo/"), StringComparison.OrdinalIgnoreCase);

    internal static string SafeMethod(string method) => method switch
    {
        "POST" or "GET" or "HEAD" or "OPTIONS" or "PUT" or "PATCH" or "DELETE" => method,
        _ => "other"
    };

    [LoggerMessage(9200, LogLevel.Information, "Agent ingress request.begin route=/api/messages/obo method={Method} requestHash={RequestHash}")]
    private static partial void LogBegin(ILogger logger, string method, string requestHash);

    [LoggerMessage(9201, LogLevel.Information, "Agent ingress request.end route=/api/messages/obo requestHash={RequestHash} status={Status} elapsedMs={ElapsedMs} cancellationRequested={CancellationRequested}")]
    private static partial void LogEnd(ILogger logger, string requestHash, int status, long elapsedMs, bool cancellationRequested);

    [LoggerMessage(9202, LogLevel.Warning, "Agent ingress request.pending route=/api/messages/obo requestHash={RequestHash} elapsedMs={ElapsedMs}")]
    private static partial void LogSlow(ILogger logger, string requestHash, long elapsedMs);

    [LoggerMessage(9203, LogLevel.Error, "Agent ingress request.failed route=/api/messages/obo requestHash={RequestHash} exceptionType={ExceptionType}")]
    private static partial void LogFailure(ILogger logger, string requestHash, string exceptionType);

    [LoggerMessage(9204, LogLevel.Warning, "Agent ingress authentication.failed route=/api/messages/obo requestHash={RequestHash} exceptionType={ExceptionType}")]
    internal static partial void LogAuthenticationFailed(ILogger logger, string requestHash, string exceptionType);
}
