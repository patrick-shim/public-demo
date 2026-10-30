using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace Lges.Ama.AgentHost;

public sealed class AgentIdentityBearerTokenHandler(
    AgentIdentityTokenContext tokenContext,
    IOptions<InternalMcpOptions> options) : DelegatingHandler
{
    private readonly InternalMcpOptions _options = options.Value;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (_options.UseAuthentication)
        {
            var token = tokenContext
                .RequireCurrent()
                .RequireAccessToken([_options.GetDelegatedScope()]);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
