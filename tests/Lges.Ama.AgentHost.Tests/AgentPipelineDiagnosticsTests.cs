using System.Net;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Lges.Ama.AgentHost;
using ModelActivity = Microsoft.Agents.Core.Models.Activity;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Lges.Ama.AgentHost.Tests;

[TestClass]
public sealed class AgentPipelineDiagnosticsTests
{
    private const string Canary = "private-payload-token-canary";

    [TestMethod]
    public void AdapterInterfaceDispatchUsesDiagnosticEntryPointInsteadOfInheritedSdkMethod()
    {
        var mapping = typeof(LgesAmaApplication).GetInterfaceMap(typeof(IAgent));
        var index = Array.FindIndex(mapping.InterfaceMethods, method => method.Name == nameof(IAgent.OnTurnAsync));
        Assert.IsGreaterThanOrEqualTo(0, index);
        Assert.AreEqual(typeof(LgesAmaApplication), mapping.TargetMethods[index].DeclaringType);
    }

    [TestMethod]
    public async Task BeginIsRecordedBeforeSdkCallbackAndSendHookDoesNotLogPayloads()
    {
        var logger = new RecordingLogger();
        using var context = CreateContext(new RecordingAdapter());
        await AgentPipelineDiagnostics.RunAsync(context, logger, async (turn, cancellationToken) =>
        {
            // The wrapped callback is the SDK OnTurnAsync, including its automatic sign-in.
            Assert.IsTrue(logger.Messages[0].Contains("turn.begin", StringComparison.Ordinal));
            await turn.SendActivityAsync(MessageFactory.Text(Canary), cancellationToken);
        }, TestContext.CancellationToken);

        Assert.IsTrue(logger.Messages[1].Contains("send.begin", StringComparison.Ordinal));
        Assert.IsTrue(logger.Messages[2].Contains("send.end", StringComparison.Ordinal));
        Assert.IsTrue(logger.Messages[^1].Contains("turn.end", StringComparison.Ordinal));
        AssertPrivateDataAbsent(logger);
    }

    [TestMethod]
    public async Task FailedSendRecordsStatusAndRethrowsWithoutLoggingExceptionMessage()
    {
        var logger = new RecordingLogger();
        using var context = CreateContext(new RecordingAdapter
        {
            Failure = new HttpRequestException(Canary, null, HttpStatusCode.Forbidden)
        });
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => AgentPipelineDiagnostics.RunAsync(
            context, logger,
            async (turn, cancellationToken) => { await turn.SendActivityAsync(MessageFactory.Text(Canary), cancellationToken); },
            TestContext.CancellationToken));

        Assert.IsTrue(logger.Messages.Any(message => message.Contains("send.failed", StringComparison.Ordinal) && message.Contains("status=403", StringComparison.Ordinal)));
        Assert.IsTrue(logger.Messages.Any(message => message.Contains("turn.failed", StringComparison.Ordinal)));
        AssertPrivateDataAbsent(logger);
    }

    [TestMethod]
    public void OnlyKnownProtocolValuesAndAuthenticationErrorCodesAreLogged()
    {
        Assert.AreEqual("other", AgentPipelineDiagnostics.SafeActivityName(Canary));
        Assert.AreEqual("other", AgentPipelineDiagnostics.SafeActivityType(Canary));
        Assert.AreEqual("other", AgentPipelineDiagnostics.SafeChannel(Canary));
        Assert.AreEqual("signin/tokenExchange", AgentPipelineDiagnostics.SafeActivityName("signin/tokenExchange"));
        var known = AgentPipelineDiagnostics.ClassifyFailure(new MsalException("invalid_client", Canary));
        var unknown = AgentPipelineDiagnostics.ClassifyFailure(new MsalException(Canary, Canary));
        Assert.AreEqual("invalid_client", known.ErrorCode);
        Assert.AreEqual("other_msal_error", unknown.ErrorCode);
    }

    private static TurnContext CreateContext(IChannelAdapter adapter) => new(adapter, new ModelActivity
    {
        Id = Canary,
        Type = ActivityTypes.Message,
        Text = Canary,
        Name = Canary,
        ChannelId = "msteams",
        Conversation = new ConversationAccount { Id = Canary },
        From = new ChannelAccount { Id = Canary },
        Recipient = new ChannelAccount { Id = "agent" },
        ServiceUrl = "https://connector.invalid/",
        ChannelData = new { secret = Canary }
    });

    private static void AssertPrivateDataAbsent(RecordingLogger logger)
    {
        Assert.DoesNotContain(Canary, string.Join('\n', logger.Messages), StringComparison.Ordinal);
        Assert.IsTrue(logger.Exceptions.All(exception => exception is null));
    }

    private sealed class RecordingAdapter : ChannelAdapter
    {
        public Exception? Failure { get; init; }

        public override Task<ResourceResponse[]> SendActivitiesAsync(ITurnContext turnContext, IActivity[] activities, CancellationToken cancellationToken) =>
            Failure is not null
                ? Task.FromException<ResourceResponse[]>(Failure)
                : Task.FromResult(activities.Select(_ => new ResourceResponse("response-id")).ToArray());
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public List<Exception?> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Exceptions.Add(exception);
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
