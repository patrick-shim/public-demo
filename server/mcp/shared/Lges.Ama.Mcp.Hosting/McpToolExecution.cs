using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace Lges.Ama.Mcp.Hosting;

public static partial class McpToolExecution
{
    public static async Task<T> RunAsync<T>(
        string operation,
        ILogger logger,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            return await action(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = Classify(exception);
            LogToolFailure(logger, operation, failure.Code, exception.GetType().Name);
            throw new McpException($"{failure.Message} Error code: {failure.Code}.");
        }
    }

    private static McpFailure Classify(Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException => new(
                "MCP-AUTHZ-001",
                "The provider rejected this operation."),
            TimeoutException or OperationCanceledException => new(
                "MCP-DEP-001",
                "The provider timed out."),
            JsonException or InvalidDataException or FormatException => new(
                "MCP-DEP-003",
                "The provider returned an invalid response."),
            HttpRequestException request when request.StatusCode == HttpStatusCode.Unauthorized => new(
                "MCP-AUTH-001",
                "The provider could not authenticate this operation."),
            HttpRequestException request when request.StatusCode == HttpStatusCode.Forbidden => new(
                "MCP-AUTHZ-001",
                "The provider rejected this operation."),
            HttpRequestException request when request.StatusCode == HttpStatusCode.TooManyRequests => new(
                "MCP-DEP-002",
                "The provider is busy."),
            HttpRequestException => new(
                "MCP-DEP-004",
                "The provider is temporarily unavailable."),
            _ => new(
                "MCP-INT-001",
                "The tool could not complete the operation.")
        };

    [LoggerMessage(
        EventId = 5001,
        Level = LogLevel.Warning,
        Message = "MCP tool operation {Operation} failed safely: code={FailureCode}, exceptionType={ExceptionType}.")]
    private static partial void LogToolFailure(
        ILogger logger,
        string operation,
        string failureCode,
        string exceptionType);

    private sealed record McpFailure(string Code, string Message);
}
