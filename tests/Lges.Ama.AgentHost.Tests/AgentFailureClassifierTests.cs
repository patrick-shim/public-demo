using System.Net;
using System.Text.Json;
using Lges.Ama.AgentHost;

namespace Lges.Ama.AgentHost.Tests;

[TestClass]
public sealed class AgentFailureClassifierTests
{
    public static IEnumerable<object[]> KnownFailures =>
    [
        [new TimeoutException("canary"), "Timeout", "STA-DEP-001"],
        [new OperationCanceledException("canary"), "Timeout", "STA-DEP-001"],
        [new HttpRequestException("canary", null, HttpStatusCode.Unauthorized), "Authentication", "STA-AUTH-001"],
        [new HttpRequestException("canary", null, HttpStatusCode.Forbidden), "Authorization", "STA-AUTHZ-001"],
        [new HttpRequestException("canary", null, HttpStatusCode.TooManyRequests), "Throttled", "STA-DEP-002"],
        [new HttpRequestException("canary", null, HttpStatusCode.ServiceUnavailable), "Unavailable", "STA-DEP-004"],
        [new JsonException("canary"), "InvalidResponse", "STA-DEP-003"]
    ];

    [TestMethod]
    [DynamicData(nameof(KnownFailures))]
    public void ClassifiesKnownFailures(
        Exception exception,
        string expectedKind,
        string expectedCode)
    {
        var failure = AgentFailureClassifier.Classify(exception);

        Assert.AreEqual(expectedKind, failure.Kind.ToString());
        Assert.AreEqual(expectedCode, failure.Code);
        Assert.DoesNotContain("canary", failure.UserMessage, StringComparison.Ordinal);
    }

    [TestMethod]
    public void UsesBoundaryFallbackForUnknownFailure()
    {
        var failure = AgentFailureClassifier.Classify(
            new InvalidOperationException("canary"),
            AgentFailureKind.Unavailable);

        Assert.AreEqual(AgentFailureKind.Unavailable, failure.Kind);
        Assert.AreEqual("STA-DEP-004", failure.Code);
        Assert.DoesNotContain("canary", failure.UserMessage, StringComparison.Ordinal);
    }
}
