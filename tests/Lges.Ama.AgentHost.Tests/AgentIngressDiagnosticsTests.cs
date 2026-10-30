using System.Text;
using Lges.Ama.AgentHost;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lges.Ama.AgentHost.Tests;

[TestClass]
public sealed class AgentIngressDiagnosticsTests
{
    private const string Canary = "private-header-body-query-canary";

    [TestMethod]
    public async Task UnauthorizedRequestIsObservedBeforeEndpointWithoutReadingHeadersBodyOrQuery()
    {
        using var logs = new RecordingLogs();
        await using var services = CreateServices(logs);
        var reachedEndpoint = false;
        var builder = new ApplicationBuilder(services);
        builder.UseMiddleware<AgentIngressDiagnosticsMiddleware>();
        builder.UseAuthentication();
        builder.UseAuthorization();
        builder.Run(context =>
        {
            reachedEndpoint = true;
            return Task.CompletedTask;
        });
        var context = CreateContext(services);
        context.SetEndpoint(new Endpoint(null,
            new EndpointMetadataCollection(new AuthorizeAttribute { Policy = AgentFrontendAuthorization.OnBehalfOfPolicy }), "obo"));

        await builder.Build()(context);

        Assert.IsFalse(reachedEndpoint);
        Assert.AreEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.IsTrue(logs.Events.Any(item => item.Id == 9200));
        Assert.IsTrue(logs.Events.Any(item => item.Id == 9201 && item.Message.Contains("status=401", StringComparison.Ordinal)));
        Assert.AreEqual(0, context.Request.Body.Position);
        Assert.DoesNotContain(Canary, string.Join('\n', logs.Events.Select(item => item.Message)), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task UnrelatedPathIsNotObserved()
    {
        using var logs = new RecordingLogs();
        await using var services = CreateServices(logs);
        var context = CreateContext(services);
        context.Request.Path = "/privacy/" + Canary;
        var nextCalled = false;
        var middleware = new AgentIngressDiagnosticsMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, services.GetRequiredService<ILogger<AgentIngressDiagnosticsMiddleware>>());
        await middleware.InvokeAsync(context);
        Assert.IsTrue(nextCalled);
        Assert.IsEmpty(logs.Events);
    }

    [TestMethod]
    public async Task PendingRequestIsObservedOnceWithoutBeingCancelled()
    {
        using var logs = new RecordingLogs();
        await using var services = CreateServices(logs);
        var context = CreateContext(services);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = new AgentIngressDiagnosticsMiddleware(_ => release.Task,
            services.GetRequiredService<ILogger<AgentIngressDiagnosticsMiddleware>>())
        {
            SlowRequestThreshold = TimeSpan.Zero
        };
        var processing = middleware.InvokeAsync(context);
        Assert.AreEqual(1, logs.Events.Count(item => item.Id == 9202));
        Assert.IsFalse(processing.IsCompleted);
        Assert.IsFalse(context.RequestAborted.IsCancellationRequested);
        release.SetResult();
        await processing;
        Assert.AreEqual(1, logs.Events.Count(item => item.Id == 9202));
        Assert.IsTrue(logs.Events.Any(item => item.Id == 9201));
    }

    [TestMethod]
    public async Task JwtFailureLogsOnlyExceptionTypeAndRetainsOriginalMessageReceivedHandler()
    {
        using var logs = new RecordingLogs();
        await using var services = CreateServices(logs);
        var context = CreateContext(services);
        var options = services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(AgentFrontendAuthorization.OnBehalfOfScheme);
        var scheme = new AuthenticationScheme(AgentFrontendAuthorization.OnBehalfOfScheme, null, typeof(JwtBearerHandler));
        Assert.IsNotNull(options.Events.OnMessageReceived);
        var failure = new AuthenticationFailedContext(context, scheme, options)
        {
            Exception = new InvalidOperationException(Canary)
        };
        await options.Events.OnAuthenticationFailed(failure);
        Assert.IsTrue(logs.Events.Any(item => item.Id == 9204 && item.Message.Contains("InvalidOperationException", StringComparison.Ordinal)));
        Assert.DoesNotContain(Canary, string.Join('\n', logs.Events.Select(item => item.Message)), StringComparison.Ordinal);
        Assert.IsTrue(logs.Events.All(item => item.Exception is null));
        Assert.IsNull(failure.Result); // Observation must not turn a failed token into authentication success.
    }

    private static ServiceProvider CreateServices(RecordingLogs logs)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TokenValidation:Enabled"] = "true",
            ["TokenValidation:TenantId"] = "11111111-1111-4111-8111-111111111111",
            ["TokenValidation:Audiences:OnBehalfOf"] = "22222222-2222-4222-8222-222222222222"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ClearProviders().AddProvider(logs));
        services.AddAgentAspNetAuthentication(configuration);
        services.AddAgentFrontendAuthorization();
        services.AddRouting();
        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext CreateContext(IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services, TraceIdentifier = Canary };
        context.Request.Path = "/api/messages/obo";
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString("?secret=" + Canary);
        context.Request.Headers["X-Private"] = Canary;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(Canary));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class RecordingLogs : ILoggerProvider
    {
        public List<LogEntry> Events { get; } = [];
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Events);
        public void Dispose() { }
    }

    private sealed record LogEntry(int Id, string Message, Exception? Exception);

    private sealed class RecordingLogger(List<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Add(new LogEntry(eventId.Id, formatter(state, exception), exception));
    }
}
