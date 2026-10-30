using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Lges.Ama.AgentHost;

internal static class AgentHealthResponseWriter
{
    public static Task WriteAsync(HttpContext context, HealthReport report) =>
        context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString().ToLowerInvariant()
        });
}
