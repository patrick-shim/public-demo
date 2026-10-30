using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Lges.Ama.AgentHost;

namespace Lges.Ama.AgentHost.Tests;

[TestClass]
public sealed class ProductionAgentConstructionTests
{
    [TestMethod]
    public async Task ProductionAgentOptionsAndMessageRoutesResolveWithOnlyOboAuthorizationConfigured()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        var values = new Dictionary<string, string?>();
        foreach (var connection in new[] { "ServiceConnection", "OboServiceConnection", "OboChannelConnection" })
        {
            values[$"Connections:{connection}:Settings:AuthorityEndpoint"] = "https://login.microsoftonline.com/11111111-1111-4111-8111-111111111111";
            values[$"Connections:{connection}:Settings:ClientId"] = "22222222-2222-4222-8222-222222222222";
            values[$"Connections:{connection}:Settings:FederatedClientId"] = "33333333-3333-4333-8333-333333333333";
        }
        builder.Configuration.AddInMemoryCollection(values);
        builder.Services.AddSingleton<IStorage, MemoryStorage>();
        builder.Services.AddHttpClient().ConfigureHttpClientDefaults(client =>
            client.ConfigurePrimaryHttpMessageHandler(() => new RejectNetworkHandler()));
        builder.AddAgentApplicationOptions();
        builder.AddAgent<LgesAmaApplication>();

        // Exercise the real SDK options, adapter and LgesAmaApplication constructor. These
        // dependencies are not used until a turn; supplying null prevents model/tool setup.
        builder.Services.AddTransient<IAgent>(services => new LgesAmaApplication(
            services.GetRequiredService<AgentApplicationOptions>(),
            chatClientFactory: null!, internalMcpTools: null!, frontendContext: null!,
            turnCoordinator: null!, frontendIdentityBinding: null!, tokenContext: null!,
            toolUserContext: null!, observabilityTokenCache: null!, oboTokenExchange: null!,
            agentHostOptions: Options.Create(new AgentHostOptions()),
            agent365Options: Options.Create(new Agent365HostOptions()),
            purviewDlpOptions: Options.Create(new PurviewDlpOptions()),
            promptShieldGuard: null!, promptShieldOptions: Options.Create(new PromptShieldOptions()),
            internalMcpOptions: Options.Create(new InternalMcpOptions { Enabled = true, UseAuthentication = true }),
            authorizationOptions: Options.Create(new AgentIdentityAuthorizationOptions()),
            oboOptions: Options.Create(new AgentIdentityOboOptions()),
            configuration: builder.Configuration,
            environment: builder.Environment,
            logger: NullLogger<LgesAmaApplication>.Instance));

        await using var app = builder.Build();
        Assert.IsInstanceOfType<LgesAmaApplication>(app.Services.GetRequiredService<IAgent>());
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new AssertFailedException("Constructor resolution must not call an external service.");
    }
}
