using Microsoft.Agents.Builder;
using Microsoft.Agents.Hosting.AspNetCore;
using System.Diagnostics;

namespace Lges.Ama.AgentHost;

public static class AgentMessageEndpointExtensions
{
    public static RouteHandlerBuilder MapAgentMessageEndpoint(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        AgentFrontendMode mode) =>
        endpoints.MapPost(
            pattern,
            async (
                HttpRequest request,
                HttpResponse response,
                IAgentHttpAdapter adapter,
                IAgent agent,
                AgentTurnFrontendContext frontendContext,
                ILoggerFactory loggerFactory,
                CancellationToken cancellationToken) =>
            {
                var logger = loggerFactory.CreateLogger("Lges.Ama.AgentHost.AgentMessageEndpoint");
                var requestHash = AgentTurnDiagnostics.HashIdentifier(request.HttpContext.TraceIdentifier);
                var timer = Stopwatch.StartNew();
                AgentPipelineDiagnostics.LogRequestBegin(logger, mode, requestHash);
                using var frontendScope = frontendContext.Push(mode);
                try
                {
                    await adapter.ProcessAsync(request, response, agent, cancellationToken);
                    AgentPipelineDiagnostics.LogRequestEnd(logger, mode, requestHash, response.StatusCode, timer.ElapsedMilliseconds);
                }
                catch (Exception exception)
                {
                    AgentPipelineDiagnostics.LogFailure(logger, "request.failed", requestHash, exception);
                    throw;
                }
            });
}
