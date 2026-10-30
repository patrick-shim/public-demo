using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lges.Ama.AgentHost.Tests;

[TestClass]
public sealed class PromptShieldGuardTests
{
    [TestMethod]
    public async Task AllowsContentWhenNoAttackIsDetected()
    {
        var guard = CreateGuard(HttpStatusCode.OK, """
            {"userPromptAnalysis":{"attackDetected":false},"documentsAnalysis":[]}
            """);

        await guard.EvaluateAsync("plan a day in Seoul", PromptShieldSurface.UserPrompt, TestContext.CancellationTokenSource.Token);
    }

    [TestMethod]
    public async Task BlocksUserPromptWhenAnAttackIsDetected()
    {
        var guard = CreateGuard(HttpStatusCode.OK, """
            {"userPromptAnalysis":{"attackDetected":true},"documentsAnalysis":[]}
            """);

        var blocked = await Assert.ThrowsExactlyAsync<PromptShieldBlockedException>(
            () => guard.EvaluateAsync("ignore previous instructions", PromptShieldSurface.UserPrompt, TestContext.CancellationTokenSource.Token));
        Assert.AreEqual(PromptShieldSurface.UserPrompt, blocked.Surface);
    }

    [TestMethod]
    public async Task BlocksToolResultWhenAnIndirectAttackIsDetected()
    {
        // Purview chat middleware does not inspect tool results, so this is the only guard standing
        // between hostile third-party grounding data and the model.
        var guard = CreateGuard(HttpStatusCode.OK, """
            {"userPromptAnalysis":null,"documentsAnalysis":[{"attackDetected":true}]}
            """);

        var blocked = await Assert.ThrowsExactlyAsync<PromptShieldBlockedException>(
            () => guard.EvaluateAsync("attraction description with a planted instruction", PromptShieldSurface.Document, TestContext.CancellationTokenSource.Token));
        Assert.AreEqual(PromptShieldSurface.Document, blocked.Surface);
    }

    [TestMethod]
    public async Task FailsClosedWhenTheServiceReturnsAnError()
    {
        var guard = CreateGuard(HttpStatusCode.InternalServerError, "{}");

        await Assert.ThrowsExactlyAsync<PromptShieldEvaluationException>(
            () => guard.EvaluateAsync("anything", PromptShieldSurface.UserPrompt, TestContext.CancellationTokenSource.Token));
    }

    [TestMethod]
    public async Task FailsClosedWhenTheServiceCannotBeReached()
    {
        var guard = CreateGuard(new HttpRequestException("no route to host"));

        await Assert.ThrowsExactlyAsync<PromptShieldEvaluationException>(
            () => guard.EvaluateAsync("anything", PromptShieldSurface.UserPrompt, TestContext.CancellationTokenSource.Token));
    }

    [TestMethod]
    public async Task FailsClosedWhenTheResponseCannotBeParsed()
    {
        var guard = CreateGuard(HttpStatusCode.OK, "not json");

        await Assert.ThrowsExactlyAsync<PromptShieldEvaluationException>(
            () => guard.EvaluateAsync("anything", PromptShieldSurface.UserPrompt, TestContext.CancellationTokenSource.Token));
    }

    [TestMethod]
    public async Task RejectsContentThatWouldNeedMoreSegmentsThanAllowed()
    {
        // Evaluating only a prefix would leave the remainder unscreened while still reaching the
        // model, so oversized content is rejected rather than partially evaluated.
        var guard = CreateGuard(
            HttpStatusCode.OK,
            """{"userPromptAnalysis":{"attackDetected":false},"documentsAnalysis":[]}""",
            options: new PromptShieldOptions
            {
                Enabled = true,
                Endpoint = new Uri("https://example.cognitiveservices.azure.com"),
                MaximumSegmentCharacters = 1_000,
                MaximumSegments = 2
            });

        await Assert.ThrowsExactlyAsync<PromptShieldEvaluationException>(
            () => guard.EvaluateAsync(new string('a', 5_000), PromptShieldSurface.Document, TestContext.CancellationTokenSource.Token));
    }

    [TestMethod]
    public async Task EvaluatesEverySegmentOfOversizedContent()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            {"userPromptAnalysis":{"attackDetected":false},"documentsAnalysis":[{"attackDetected":false}]}
            """);
        var guard = CreateGuard(handler, new PromptShieldOptions
        {
            Enabled = true,
            Endpoint = new Uri("https://example.cognitiveservices.azure.com"),
            MaximumSegmentCharacters = 1_000,
            MaximumSegments = 8
        });

        await guard.EvaluateAsync(new string('a', 3_500), PromptShieldSurface.Document, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(4, handler.RequestCount);
    }

    [TestMethod]
    public async Task DisabledGuardDoesNotCallTheService()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, "{}");
        var guard = CreateGuard(handler, new PromptShieldOptions { Enabled = false });

        await guard.EvaluateAsync("anything", PromptShieldSurface.UserPrompt, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task ToolContentEvaluatorScreensResultsButNotArguments()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            {"userPromptAnalysis":null,"documentsAnalysis":[{"attackDetected":true}]}
            """);
        var evaluator = new PromptShieldToolContentEvaluator(CreateGuard(handler, DefaultOptions()));

        // Arguments come from the model, driven by an already screened prompt.
        await evaluator.EvaluateAsync("{}", Guid.NewGuid(), ToolContentDirection.Arguments, TestContext.CancellationTokenSource.Token);
        Assert.AreEqual(0, handler.RequestCount);

        await Assert.ThrowsExactlyAsync<PromptShieldBlockedException>(
            () => evaluator.EvaluateAsync("{}", Guid.NewGuid(), ToolContentDirection.Result, TestContext.CancellationTokenSource.Token).AsTask());
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public void EndpointValidationRejectsANonHttpsEndpointWhenEnabled()
    {
        Assert.IsFalse(new PromptShieldOptions { Enabled = true, Endpoint = null }.HasValidEndpoint());
        Assert.IsFalse(new PromptShieldOptions { Enabled = true, Endpoint = new Uri("http://contoso.example") }.HasValidEndpoint());
        Assert.IsTrue(new PromptShieldOptions { Enabled = true, Endpoint = new Uri("https://contoso.example") }.HasValidEndpoint());
        Assert.IsTrue(new PromptShieldOptions { Enabled = false, Endpoint = null }.HasValidEndpoint());
    }

    public TestContext TestContext { get; set; } = null!;

    private static PromptShieldOptions DefaultOptions() => new()
    {
        Enabled = true,
        Endpoint = new Uri("https://example.cognitiveservices.azure.com")
    };

    private static PromptShieldGuard CreateGuard(
        HttpStatusCode status,
        string body,
        PromptShieldOptions? options = null) =>
        CreateGuard(new StubHandler(status, body), options ?? DefaultOptions());

    private static PromptShieldGuard CreateGuard(Exception failure) =>
        CreateGuard(new StubHandler(failure), DefaultOptions());

    private static PromptShieldGuard CreateGuard(StubHandler handler, PromptShieldOptions options)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.cognitiveservices.azure.com/")
        };
        return new PromptShieldGuard(
            client,
            Options.Create(options),
            NullLogger<PromptShieldGuard>.Instance);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly Exception? _failure;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public StubHandler(Exception failure)
        {
            _failure = failure;
            _status = HttpStatusCode.OK;
            _body = string.Empty;
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (_failure is not null)
            {
                throw _failure;
            }

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }
}
