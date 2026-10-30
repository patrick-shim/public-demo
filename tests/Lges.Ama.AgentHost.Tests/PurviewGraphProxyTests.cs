using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Lges.Ama.AgentHost;

namespace Lges.Ama.AgentHost.Tests;

[TestClass]
public sealed class PurviewGraphProxyTests
{
    private static readonly Guid TestUserId = Guid.Parse("11111111-1111-4111-8111-111111111111");

    [TestMethod]
    public async Task RejectsNonLoopbackRequestsWithoutForwarding()
    {
        var handler = new RecordingHandler();
        var proxy = CreateProxy(handler);
        var context = CreateContext(IPAddress.Parse("10.0.0.1"));

        await proxy.ForwardAsync(
            context,
            $"users/{TestUserId}/dataSecurityAndGovernance/processContent",
            CancellationToken.None);

        Assert.AreEqual(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.AreEqual(0, handler.CallCount);
    }

    [TestMethod]
    public async Task LoopbackRequestForwardsGraphResponseWithoutExposingRoute()
    {
        const string graphResponse =
            "{\"error\":{\"code\":\"BadRequest\",\"message\":\"Invalid correlationId.\"}}";
        var handler = new RecordingHandler(graphResponse);
        var proxy = CreateProxy(handler);
        var context = CreateContext(IPAddress.Loopback);
        context.Request.Headers.Authorization = "Bearer test-token";

        await proxy.ForwardAsync(
            context,
            $"users/{TestUserId}/dataSecurityAndGovernance/processContent",
            CancellationToken.None);

        Assert.AreEqual(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.AreEqual(1, handler.CallCount);
        Assert.AreEqual(
            $"https://graph.microsoft.com/v1.0/users/{TestUserId}/dataSecurityAndGovernance/processContent",
            handler.RequestUri?.AbsoluteUri);
        Assert.AreEqual("Bearer test-token", handler.Authorization);
        Assert.IsTrue(Guid.TryParse(handler.ClientRequestId, out _));
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        Assert.AreEqual(graphResponse, await reader.ReadToEndAsync());
    }

    [TestMethod]
    public async Task EmptyTextFunctionIntermediateDoesNotCallGraph()
    {
        var handler = new RecordingHandler();
        var proxy = CreateProxy(handler);
        var context = CreateContext(IPAddress.Loopback);
        var correlationId = Guid.NewGuid();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes($$"""
                        {
                            "contentToProcess": {
                                "contentEntries": [
                                    {
                                        "correlationId": "{{correlationId}}",
                                        "content": {
                                            "@odata.type": "microsoft.graph.textContent",
                                            "data": ""
                                        }
                                    }
                                ]
                            }
                        }
                        """));

        await proxy.ForwardAsync(
                context,
                $"users/{TestUserId}/dataSecurityAndGovernance/processContent",
                CancellationToken.None);

        Assert.AreEqual(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.AreEqual(0, handler.CallCount);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        using var response = JsonDocument.Parse(await reader.ReadToEndAsync());
        Assert.AreEqual(0, response.RootElement.GetProperty("policyActions").GetArrayLength());
    }

    [TestMethod]
    [DataRow("evaluateInline", "evaluateInline")]
    [DataRow(" EVALUATEINLINE ", "evaluateInline")]
    [DataRow("respond-async, evaluateInline", "evaluateInline")]
    [DataRow("respond-async", null)]
    [DataRow("evaluateInline=untrusted", null)]
    public async Task ProcessContentForwardsOnlySdkInlinePreference(string preference, string? expected)
    {
        var handler = new RecordingHandler("{\"policyActions\":[],\"processingErrors\":[]}", HttpStatusCode.OK);
        var proxy = CreateProxy(handler);
        var context = CreateContext(IPAddress.Loopback);
        context.Request.Headers["Prefer"] = preference;

        await proxy.ForwardAsync(
            context,
            $"users/{TestUserId}/dataSecurityAndGovernance/processContent",
            CancellationToken.None);

        Assert.AreEqual(expected, handler.Prefer);
    }

    [TestMethod]
    public async Task InlinePreferenceDoesNotApplyToScopeDiscovery()
    {
        var handler = new RecordingHandler("{}", HttpStatusCode.OK);
        var context = CreateContext(IPAddress.Loopback);
        context.Request.Headers["Prefer"] = "evaluateInline";

        await CreateProxy(handler).ForwardAsync(
            context,
            $"users/{TestUserId}/dataSecurityAndGovernance/protectionScopes/compute",
            CancellationToken.None);

        Assert.IsNull(handler.Prefer);
    }

    [TestMethod]
    public async Task SuccessfulEvaluationLogsOnlyPolicyMetadataAndPreservesResponse()
    {
        const string applicationId = "22222222-2222-4222-8222-222222222222";
        const string contentMarker = "private synthetic test content";
        const string tokenMarker = "Bearer private-test-token";
        const string responseBody = """
            {"protectionScopeState":"modified","policyActions":[{"action":"blockAccess"},{"action":"restrictAccess","restrictionAction":"block"},{"action":"audit"}],"processingErrors":[]}
            """;
        var handler = new RecordingHandler(responseBody, HttpStatusCode.OK);
        var logger = new RecordingLogger();
        var context = CreateContext(IPAddress.Loopback);
        context.Request.Headers.Authorization = tokenMarker;
        context.Request.Headers["Prefer"] = "evaluateInline";
        var requestBody = $$$"""
            {"contentToProcess":{"activityMetadata":{"activity":"uploadText"},"protectedAppMetadata":{"applicationLocation":{"value":"{{{applicationId}}}"}},"contentEntries":[{"content":{"@odata.type":"microsoft.graph.textContent","data":"{{{contentMarker}}}"}}]}}
            """;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(requestBody));

        await CreateProxy(handler, logger).ForwardAsync(
            context,
            $"users/{TestUserId}/dataSecurityAndGovernance/processContent",
            CancellationToken.None);

        var entry = logger.Entries.Single();
        Assert.AreEqual(1021, entry.EventId);
        Assert.AreEqual("uploadText", entry.Fields["Activity"]);
        Assert.AreEqual(applicationId, entry.Fields["ApplicationId"]);
        Assert.AreEqual(200, entry.Fields["StatusCode"]);
        Assert.AreEqual(3, entry.Fields["ActionCount"]);
        Assert.AreEqual(2, entry.Fields["BlockCount"]);
        Assert.AreEqual(0, entry.Fields["ErrorCount"]);
        Assert.AreEqual("modified", entry.Fields["ScopeState"]);
        Assert.IsTrue((bool)entry.Fields["InlinePreferenceForwarded"]!);
        foreach (var value in new[] { contentMarker, tokenMarker, TestUserId.ToString() })
        {
            Assert.DoesNotContain(value, entry.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(value, JsonSerializer.Serialize(entry.Fields), StringComparison.Ordinal);
        }

        Assert.AreEqual(requestBody, handler.RequestBody);
        Assert.AreEqual(responseBody, await ReadResponseAsync(context));
        Assert.AreEqual(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [TestMethod]
    public async Task AcceptedOrUnrecognizedEvaluationDoesNotBecomeAnAllowDecisionInDiagnostics()
    {
        const string privateMarker = "untrusted metadata";
        const string responseBody = $$"""{"protectionScopeState":"{{privateMarker}}"}""";
        var handler = new RecordingHandler(responseBody, HttpStatusCode.Accepted);
        var logger = new RecordingLogger();
        var context = CreateContext(IPAddress.Loopback);
        context.Request.Headers["Prefer"] = privateMarker;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            contentToProcess = new
            {
                activityMetadata = new { activity = privateMarker },
                protectedAppMetadata = new { applicationLocation = new { value = privateMarker } }
            }
        })));

        await CreateProxy(handler, logger).ForwardAsync(
            context,
            $"users/{TestUserId}/dataSecurityAndGovernance/processContent",
            CancellationToken.None);

        var entry = logger.Entries.Single();
        Assert.AreEqual(202, entry.Fields["StatusCode"]);
        Assert.AreEqual(-1, entry.Fields["ActionCount"]);
        Assert.AreEqual(-1, entry.Fields["BlockCount"]);
        Assert.AreEqual(-1, entry.Fields["ErrorCount"]);
        Assert.AreEqual("unknown", entry.Fields["Activity"]);
        Assert.AreEqual("unknown", entry.Fields["ApplicationId"]);
        Assert.AreEqual("unknown", entry.Fields["ScopeState"]);
        Assert.IsFalse((bool)entry.Fields["InlinePreferenceForwarded"]!);
        Assert.DoesNotContain(privateMarker, entry.Text, StringComparison.Ordinal);
        Assert.AreEqual(responseBody, await ReadResponseAsync(context));
        Assert.AreEqual(StatusCodes.Status202Accepted, context.Response.StatusCode);
    }

    [TestMethod]
    public async Task EmptyAcceptedResponseRemainsIntactAndLogsUnavailableCounts()
    {
        var handler = new RecordingHandler(string.Empty, HttpStatusCode.Accepted);
        var logger = new RecordingLogger();
        var context = CreateContext(IPAddress.Loopback);

        await CreateProxy(handler, logger).ForwardAsync(
            context,
            $"users/{TestUserId}/dataSecurityAndGovernance/processContent",
            CancellationToken.None);

        Assert.AreEqual("invalid-json", logger.Entries.Single().Fields["ScopeState"]);
        Assert.AreEqual(-1, logger.Entries.Single().Fields["ActionCount"]);
        Assert.AreEqual(string.Empty, await ReadResponseAsync(context));
        Assert.AreEqual(StatusCodes.Status202Accepted, context.Response.StatusCode);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProcessingErrorsFailClosedWithoutLeakingDetails(bool inline)
    {
        const string privateError = "private processing error detail";
        var handler = new RecordingHandler(
            """{"policyActions":[],"processingErrors":[{"message":"private processing error detail"}]}""",
            HttpStatusCode.OK);
        var logger = new RecordingLogger();
        var context = CreateContext(IPAddress.Loopback);
        if (inline)
        {
            context.Request.Headers["Prefer"] = "evaluateInline";
        }

        await CreateProxy(handler, logger).ForwardAsync(
            context, $"users/{TestUserId}/dataSecurityAndGovernance/processContent", CancellationToken.None);

        Assert.AreEqual(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        Assert.IsNull(context.Response.ContentLength);
        Assert.AreEqual("application/json", context.Response.ContentType);
        var response = await ReadResponseAsync(context);
        Assert.Contains("PurviewEvaluationIncomplete", response, StringComparison.Ordinal);
        Assert.DoesNotContain(privateError, response, StringComparison.Ordinal);
        Assert.DoesNotContain(privateError, JsonSerializer.Serialize(logger.Entries), StringComparison.Ordinal);
        Assert.AreEqual(1, logger.Entries.Single(entry => entry.EventId == 1021).Fields["ErrorCount"]);
        Assert.IsTrue(logger.Entries.Any(entry => entry.EventId == 1023));
    }

    [TestMethod]
    [DataRow(202, "{\"policyActions\":[],\"processingErrors\":[]}")]
    [DataRow(200, "{}")]
    [DataRow(200, "{\"policyActions\":null,\"processingErrors\":[]}")]
    [DataRow(200, "{\"policyActions\":[],\"processingErrors\":null}")]
    [DataRow(200, "not json")]
    public async Task RequestedInlineEvaluationRequiresCompletedWellFormedResult(int status, string body)
    {
        var handler = new RecordingHandler(body, (HttpStatusCode)status);
        var context = CreateContext(IPAddress.Loopback);
        context.Request.Headers["Prefer"] = "evaluateInline";

        await CreateProxy(handler).ForwardAsync(
            context, $"users/{TestUserId}/dataSecurityAndGovernance/processContent", CancellationToken.None);

        Assert.AreEqual(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        Assert.Contains("PurviewEvaluationIncomplete", await ReadResponseAsync(context), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ValidInlineAllowResultRemainsIntact()
    {
        const string body = "{\"policyActions\":[],\"processingErrors\":[]}";
        var handler = new RecordingHandler(body, HttpStatusCode.OK);
        var context = CreateContext(IPAddress.Loopback);
        context.Request.Headers["Prefer"] = "evaluateInline";

        await CreateProxy(handler).ForwardAsync(
            context, $"users/{TestUserId}/dataSecurityAndGovernance/processContent", CancellationToken.None);

        Assert.AreEqual(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.AreEqual(body, await ReadResponseAsync(context));
    }

    [TestMethod]
    public async Task ScopeDiagnosticsContainCountsOnlyAndPreserveResponse()
    {
        const string body = """
            {"value":[
              {"activities":"uploadText, downloadText","executionMode":"evaluateInline","locations":[{"value":"private-location"}]},
              {"activities":"uploadText","executionMode":"evaluateInline"},
              {"activities":"downloadText","executionMode":"evaluateOffline"},
              {"activities":"private-activity","executionMode":"private-mode"}
            ]}
            """;
        var handler = new RecordingHandler(body, HttpStatusCode.OK);
        var logger = new RecordingLogger();
        var context = CreateContext(IPAddress.Loopback);

        await CreateProxy(handler, logger).ForwardAsync(
            context, $"users/{TestUserId}/dataSecurityAndGovernance/protectionScopes/compute", CancellationToken.None);

        var entry = logger.Entries.Single();
        Assert.AreEqual(1022, entry.EventId);
        Assert.AreEqual(4, entry.Fields["TotalCount"]);
        Assert.AreEqual(2, entry.Fields["UploadInlineCount"]);
        Assert.AreEqual(1, entry.Fields["DownloadInlineCount"]);
        Assert.AreEqual(1, entry.Fields["OfflineCount"]);
        Assert.AreEqual(200, entry.Fields["StatusCode"]);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(entry), StringComparison.Ordinal);
        Assert.DoesNotContain(TestUserId.ToString(), JsonSerializer.Serialize(entry), StringComparison.Ordinal);
        Assert.AreEqual(body, await ReadResponseAsync(context));
    }

    private static async Task<string> ReadResponseAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static PurviewGraphProxy CreateProxy(RecordingHandler handler, ILogger<PurviewGraphProxy>? logger = null) =>
        new(
            new TestHttpClientFactory(handler),
            Options.Create(new PurviewDlpOptions
            {
                UseCompatibilityProxy = true,
                GraphBaseUri = new Uri("https://graph.microsoft.com/v1.0/")
            }),
            logger ?? NullLogger<PurviewGraphProxy>.Instance);

    private static DefaultHttpContext CreateContext(IPAddress remoteAddress)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remoteAddress;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class TestHttpClientFactory(RecordingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(
        string? responseBody = null,
        HttpStatusCode statusCode = HttpStatusCode.BadRequest) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string? Authorization { get; private set; }

        public string? ClientRequestId { get; private set; }

        public string? Prefer { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            ClientRequestId = request.Headers.TryGetValues("Client-Request-Id", out var values)
                ? values.Single()
                : null;
            Prefer = request.Headers.TryGetValues("Prefer", out var preferences)
                ? preferences.Single()
                : null;
            RequestBody = request.Content is not null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : null;
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    responseBody ?? "{}",
                    Encoding.UTF8,
                    "application/json"),
                Headers =
                {
                    { "request-id", "test-request-id" }
                }
            };
        }
    }

    private sealed class RecordingLogger : ILogger<PurviewGraphProxy>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = ((IEnumerable<KeyValuePair<string, object?>>)(object)state!)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            Entries.Add(new LogEntry(eventId.Id, formatter(state, exception), fields));
        }
    }

    private sealed record LogEntry(int EventId, string Text, Dictionary<string, object?> Fields);
}
