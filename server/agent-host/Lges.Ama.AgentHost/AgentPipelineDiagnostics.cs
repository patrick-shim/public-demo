using System.Diagnostics;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using MsalException = Microsoft.Identity.Client.MsalException;

namespace Lges.Ama.AgentHost;

// The SDK performs user sign-in before calling our message handler. Intercept the IAgent
// entry point so queued turns and sign-in card delivery are visible without logging content.
public sealed partial class LgesAmaApplication : IAgent
{
    Task IAgent.OnTurnAsync(ITurnContext turnContext, CancellationToken cancellationToken) =>
        AgentPipelineDiagnostics.RunAsync(turnContext, _logger, base.OnTurnAsync, cancellationToken);
}

internal static partial class AgentPipelineDiagnostics
{
    internal static async Task RunAsync(
        ITurnContext context,
        ILogger logger,
        AgentCallbackHandler next,
        CancellationToken cancellationToken)
    {
        var activityHash = AgentTurnDiagnostics.HashIdentifier(context.Activity.Id);
        var timer = Stopwatch.StartNew();
        LogTurnBegin(logger,
            SafeActivityType(context.Activity.Type), SafeActivityName(context.Activity.Name),
            SafeChannel(context.Activity.ChannelId), activityHash);

        context.OnSendActivities(async (_, activities, sendNext) =>
        {
            var types = string.Join(',', activities.Select(activity => SafeActivityType(activity.Type)).Distinct(StringComparer.Ordinal));
            LogSendBegin(logger,
                activityHash, activities.Count, types);
            try
            {
                var responses = await sendNext();
                LogSendEnd(logger,
                    activityHash, responses.Length);
                return responses;
            }
            catch (Exception exception)
            {
                LogFailure(logger, "send.failed", activityHash, exception);
                throw;
            }
        });

        try
        {
            await next(context, cancellationToken);
            LogTurnEnd(logger,
                activityHash, timer.ElapsedMilliseconds, cancellationToken.IsCancellationRequested);
        }
        catch (Exception exception)
        {
            LogFailure(logger, "turn.failed", activityHash, exception);
            throw;
        }
    }

    internal static void LogFailure(ILogger logger, string stage, string activityHash, Exception exception)
    {
        var failure = ClassifyFailure(exception);
        // Do not pass the Exception to ILogger: formatters would include messages and token details.
        LogPipelineFailure(logger,
            stage, activityHash, failure.ExceptionType, failure.Status, failure.ErrorCode);
    }

    internal static PipelineFailure ClassifyFailure(Exception exception)
    {
        int? status = null;
        var errorCode = "none";
        for (var current = exception; current is not null; current = current.InnerException)
        {
            status ??= current switch
            {
                HttpRequestException http => (int?)http.StatusCode,
                Azure.RequestFailedException azure => azure.Status,
                System.ClientModel.ClientResultException client => client.Status,
                _ => null
            };
            if (current is MsalException msal)
            {
                errorCode = msal.ErrorCode switch
                {
                    "invalid_client" or "invalid_grant" or "invalid_scope" or "unauthorized_client"
                        or "interaction_required" or "consent_required" or "access_denied"
                        or "temporarily_unavailable" or "service_not_available" => msal.ErrorCode,
                    _ => "other_msal_error"
                };
            }
        }
        return new PipelineFailure(exception.GetType().Name, status, errorCode);
    }

    internal static string SafeActivityType(string? value) => value switch
    {
        "message" or "invoke" or "event" or "conversationUpdate" or "installationUpdate"
            or "messageReaction" or "messageUpdate" or "messageDelete" or "typing"
            or "endOfConversation" or "trace" or "invokeResponse" => value,
        null or "" => "none",
        _ => "other"
    };

    internal static string SafeActivityName(string? value) => value switch
    {
        "signin/verifyState" or "signin/tokenExchange" or "tokens/response"
            or "adaptiveCard/action" or "message/submitAction" => value,
        null or "" => "none",
        _ => "other"
    };

    internal static string SafeChannel(string? value) => value switch
    {
        "msteams" or "m365copilot" or "copilot" or "webchat" or "directline" or "emulator" => value,
        null or "" => "none",
        _ => "other"
    };

    internal sealed record PipelineFailure(string ExceptionType, int? Status, string ErrorCode);

    [LoggerMessage(9100, LogLevel.Information, "Agent pipeline turn.begin type={ActivityType} name={ActivityName} channel={Channel} activityHash={ActivityHash}")]
    private static partial void LogTurnBegin(ILogger logger, string activityType, string activityName, string channel, string activityHash);

    [LoggerMessage(9101, LogLevel.Information, "Agent pipeline turn.end activityHash={ActivityHash} elapsedMs={ElapsedMs} cancellationRequested={CancellationRequested}")]
    private static partial void LogTurnEnd(ILogger logger, string activityHash, long elapsedMs, bool cancellationRequested);

    [LoggerMessage(9102, LogLevel.Information, "Agent pipeline send.begin activityHash={ActivityHash} count={Count} types={ActivityTypes}")]
    private static partial void LogSendBegin(ILogger logger, string activityHash, int count, string activityTypes);

    [LoggerMessage(9103, LogLevel.Information, "Agent pipeline send.end activityHash={ActivityHash} responseCount={ResponseCount}")]
    private static partial void LogSendEnd(ILogger logger, string activityHash, int responseCount);

    [LoggerMessage(9104, LogLevel.Error, "Agent pipeline {Stage} activityHash={ActivityHash} exceptionType={ExceptionType} status={Status} errorCode={ErrorCode}")]
    private static partial void LogPipelineFailure(ILogger logger, string stage, string activityHash, string exceptionType, int? status, string errorCode);

    [LoggerMessage(9105, LogLevel.Information, "Agent endpoint request.begin frontend={Frontend} requestHash={RequestHash}")]
    internal static partial void LogRequestBegin(ILogger logger, AgentFrontendMode frontend, string requestHash);

    [LoggerMessage(9106, LogLevel.Information, "Agent endpoint request.end frontend={Frontend} requestHash={RequestHash} status={Status} elapsedMs={ElapsedMs}")]
    internal static partial void LogRequestEnd(ILogger logger, AgentFrontendMode frontend, string requestHash, int status, long elapsedMs);
}
