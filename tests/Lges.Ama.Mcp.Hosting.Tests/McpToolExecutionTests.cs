using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Lges.Ama.Mcp.Hosting;

namespace Lges.Ama.Mcp.Hosting.Tests;

[TestClass]
public sealed class McpToolExecutionTests
{
    [TestMethod]
    public async Task MapsProviderFailureWithoutLeakingDetails()
    {
        const string canary = "sensitive-provider-body";

        var exception = await Assert.ThrowsExactlyAsync<McpException>(() =>
            McpToolExecution.RunAsync<string>(
                "weather.current",
                NullLogger.Instance,
                _ => Task.FromException<string>(new HttpRequestException(
                    canary,
                    null,
                    HttpStatusCode.ServiceUnavailable)),
                TestContext.CancellationToken));

        StringAssert.Contains(exception.Message, "MCP-DEP-004");
        Assert.DoesNotContain(canary, exception.ToString(), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task PreservesValidationFailure()
    {
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            McpToolExecution.RunAsync<string>(
                "weather.forecast",
                NullLogger.Instance,
                _ => Task.FromException<string>(new ArgumentOutOfRangeException("days")),
                TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task PreservesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
            McpToolExecution.RunAsync<string>(
                "weather.current",
                NullLogger.Instance,
                token => Task.FromCanceled<string>(token),
                cancellation.Token));
    }

    public TestContext TestContext { get; set; } = null!;
}
