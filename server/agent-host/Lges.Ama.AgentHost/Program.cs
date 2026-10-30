using Microsoft.Agents.A365.Observability.Hosting.Caching;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Storage;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.OpenTelemetry;
using Lges.Ama.Agent;
using Lges.Ama.AgentHost;

var builder = WebApplication.CreateBuilder(args);

PurviewSerializationWorkaround.Apply();

builder.Services
    .AddOptions<AgentHostOptions>()
    .Bind(builder.Configuration.GetSection(AgentHostOptions.SectionName))
    .ValidateDataAnnotations();

var isLocalEnvironment = builder.Environment.IsDevelopment()
    || builder.Environment.IsEnvironment("Playground");
if (!isLocalEnvironment && !builder.Configuration.GetValue("TokenValidation:Enabled", false))
{
    throw new InvalidOperationException("Token validation must be enabled outside local development.");
}

builder.Services.AddHttpClient();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<AgentTurnCoordinator>();
var readinessState = new AgentHostReadinessState();
builder.Services.AddSingleton(readinessState);
builder.Services
    .AddHealthChecks()
    .AddCheck(
        "self",
        () => HealthCheckResult.Healthy(),
        tags: ["live"])
    .AddCheck(
        "agent-host",
        () => !readinessState.IsReady
            ? HealthCheckResult.Unhealthy()
            : readinessState.HasDurableStorage
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Degraded(
                    "Process-local session storage is active; do not scale out."),
        tags: ["ready"]);
builder.Services.AddAgentAspNetAuthentication(builder.Configuration);
builder.Services.AddAgentFrontendAuthorization();
builder.Services.AddSingleton<IStorage, MemoryStorage>();

// ==================== A365 SDK: RUNTIME, DISABLED WORKIQ GATE, AND OBSERVABILITY ======
builder.Services
    .AddOptions<Agent365HostOptions>()
    .Bind(builder.Configuration.GetSection(Agent365HostOptions.SectionName))
    .Validate(
        options => !options.EnableWorkIq,
        "Agent365:EnableWorkIq must remain false until Agent 365 Tooling supports MCP 2.1 and tool-content DLP is validated.")
    .ValidateOnStart();

var observabilityTokenCache = new ServiceTokenCache();
builder.Services.AddSingleton<IExporterTokenCache<string>>(observabilityTokenCache);
builder.UseMicrosoftOpenTelemetry(options =>
{
    options.Exporters = isLocalEnvironment
        ? ExportTarget.Console
        : ExportTarget.Agent365;
    options.Agent365.TokenResolver = observabilityTokenCache.GetObservabilityToken;
    options.Agent365.UseS2SEndpoint = true;
    options.Instrumentation.EnableAspNetCoreInstrumentation = true;
    options.Instrumentation.EnableHttpClientInstrumentation = true;
    options.Instrumentation.EnableAzureSdkInstrumentation = true;
    options.Instrumentation.EnableAgentFrameworkInstrumentation = true;
    options.Instrumentation.EnableAgent365Instrumentation = true;
});
// ==================== END A365 SDK ====================================================

// ==================== PURVIEW DLP: FAIL-CLOSED PROMPT/RESPONSE SETTINGS ================
builder.Services
    .AddOptions<PurviewDlpOptions>()
    .Bind(builder.Configuration.GetSection(PurviewDlpOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        options => !options.Enabled || Guid.TryParse(options.TenantId, out _),
        "PurviewDlp:TenantId must be a valid Entra tenant ID when Purview DLP is enabled.")
    .Validate(
        options => !options.Enabled || Guid.TryParse(options.ApplicationId, out _),
        "PurviewDlp:ApplicationId must be a valid Entra application ID when Purview DLP is enabled.")
    .Validate(
        options => !options.UseCompatibilityProxy || options.CompatibilityProxyBaseUri.IsLoopback,
        "PurviewDlp:CompatibilityProxyBaseUri must be loopback-only.")
    .ValidateOnStart();
// ==================== END PURVIEW DLP ==================================================

builder.Services
    .AddOptions<InternalMcpOptions>()
    .Bind(builder.Configuration.GetSection(InternalMcpOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        options => !options.Enabled || options.HasValidEndpoints(requireHttps: !isLocalEnvironment),
        "All three InternalMcp endpoints must be absolute HTTPS URLs outside local development.")
    .Validate(
        options => !options.Enabled || options.HasValidAudience(),
        "InternalMcp:Audience must be an api:// application ID URI when authentication is enabled.")
    .Validate(
        options => isLocalEnvironment || !options.Enabled || options.UseAuthentication,
        "Internal MCP authentication cannot be disabled outside local development.")
    .Validate(
        options => !options.Enabled || builder.Configuration.GetValue("PurviewDlp:Enabled", true),
        "Purview DLP must be enabled when internal MCP tools are enabled.")
    .ValidateOnStart();

builder.Services
    .AddOptions<AgentIdentityAuthorizationOptions>()
    .Bind(builder.Configuration.GetSection(AgentIdentityAuthorizationOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        options => isLocalEnvironment
            || options.OnBehalfOf.HasRequiredHandlerNames(false, false),
        "Foundry auth handlers are required for the OBO frontend outside local development.")
    .Validate(
        options => isLocalEnvironment
            || !builder.Configuration.GetValue("PurviewDlp:Enabled", true)
            || !string.IsNullOrWhiteSpace(options.OnBehalfOf.PurviewAuthHandlerName),
        "Purview auth handlers are required for the OBO frontend when Purview DLP is enabled.")
    .Validate(
        options => isLocalEnvironment
            || !builder.Configuration.GetValue("InternalMcp:Enabled", false)
            || !string.IsNullOrWhiteSpace(options.OnBehalfOf.InternalMcpAuthHandlerName),
        "Internal MCP auth handlers are required for the OBO frontend when internal MCP tools are enabled.")
    .ValidateOnStart();

builder.Services
    .AddOptions<AgentIdentityOboOptions>()
    .Bind(builder.Configuration.GetSection(AgentIdentityOboOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        options => isLocalEnvironment || Guid.TryParse(options.AgentId, out _),
        "AgentIdentityObo:AgentId must be the OBO child Agent Identity application ID outside local development.")
    .ValidateOnStart();

// ==================== PROMPT SHIELDS: FAIL-CLOSED PROMPT-INJECTION DEFENCE ================
// Azure AI Content Safety Prompt Shields is a standalone text classifier and is independent of
// where inference runs, so this guard survives a move to a model outside Azure.
builder.Services
    .AddOptions<PromptShieldOptions>()
    .Bind(builder.Configuration.GetSection(PromptShieldOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        options => options.HasValidEndpoint(),
        "PromptShield:Endpoint must be an absolute HTTPS Content Safety endpoint when the guard is enabled.")
    .ValidateOnStart();
// ==================== END PROMPT SHIELDS ==================================================

builder.AddAgentApplicationOptions();
builder.AddAgent<LgesAmaApplication>();

builder.Services.AddSingleton<AgentTurnFrontendContext>();
builder.Services.AddSingleton<AgentFrontendIdentityBinding>();
builder.Services.AddSingleton<IAgentIdentityParentTokenProvider, AgentIdentityParentTokenProvider>();
builder.Services.AddSingleton<IAgentIdentityChildTokenClient, MsalAgentIdentityChildTokenClient>();
builder.Services.AddSingleton<IAgentIdentityOboTokenExchange, AgentIdentityOboTokenExchange>();
builder.Services.AddSingleton<AgentIdentityTokenContext>();
builder.Services.AddSingleton<PurviewApplicationLocationResolver>();
builder.Services.AddSingleton<AgentIdentityTokenCredential>();
builder.Services.AddSingleton<PurviewAgentIdentityTokenCredential>();
builder.Services.AddTransient<AgentIdentityBearerTokenHandler>();
builder.Services
    .AddHttpClient("InternalMcp", client => client.Timeout = TimeSpan.FromSeconds(30))
    .AddHttpMessageHandler<AgentIdentityBearerTokenHandler>();
builder.Services.AddSingleton<ToolUserContext>();
builder.Services.AddTransient<PromptShieldBearerTokenHandler>();
builder.Services
    .AddHttpClient(
        PromptShieldGuard.HttpClientName,
        (services, client) =>
        {
            var shieldOptions = services.GetRequiredService<IOptions<PromptShieldOptions>>().Value;
            if (shieldOptions.Endpoint is not null)
            {
                var root = shieldOptions.Endpoint.AbsoluteUri;
                client.BaseAddress = new Uri(root.EndsWith('/') ? root : root + "/");
            }

            client.Timeout = TimeSpan.FromSeconds(shieldOptions.TimeoutSeconds);
        })
    .AddHttpMessageHandler<PromptShieldBearerTokenHandler>();
builder.Services.AddSingleton(services => new PromptShieldGuard(
    services.GetRequiredService<IHttpClientFactory>().CreateClient(PromptShieldGuard.HttpClientName),
    services.GetRequiredService<IOptions<PromptShieldOptions>>(),
    services.GetRequiredService<ILogger<PromptShieldGuard>>()));
builder.Services.AddSingleton<PurviewToolContentEvaluator>();
builder.Services.AddSingleton<PromptShieldToolContentEvaluator>();
builder.Services.AddSingleton<IToolContentEvaluator>(services =>
    new CompositeToolContentEvaluator(
    [
        services.GetRequiredService<PurviewToolContentEvaluator>(),
        services.GetRequiredService<PromptShieldToolContentEvaluator>()
    ]));
builder.Services.AddSingleton<ToolContentProtector>();
builder.Services.AddSingleton<PurviewGraphProxy>();
builder.Services.AddSingleton<InternalMcpToolCatalog>();
builder.Services.AddSingleton<AgentChatClientFactory>();

var app = builder.Build();

app.UseMiddleware<AgentIngressDiagnosticsMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

app.MapPost(
        "/internal/purview/{**path}",
        (HttpContext context, string? path, PurviewGraphProxy proxy, CancellationToken cancellationToken) =>
            proxy.ForwardAsync(context, path, cancellationToken))
    .AllowAnonymous();

var oboMessages = app.MapAgentMessageEndpoint(
    "/api/messages/obo",
    AgentFrontendMode.OnBehalfOf);
oboMessages.RequireAuthorization(AgentFrontendAuthorization.OnBehalfOfPolicy);

if (isLocalEnvironment)
{

    oboMessages.AllowAnonymous();
}

app.MapGet("/api/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();
app.MapHealthChecks(
        "/api/health/live",
        new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("live"),
            ResponseWriter = AgentHealthResponseWriter.WriteAsync
        })
    .AllowAnonymous();
app.MapHealthChecks(
        "/api/health/ready",
        new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("ready"),
            ResponseWriter = AgentHealthResponseWriter.WriteAsync
        })
    .AllowAnonymous();
app.MapGet("/", () => Results.Text(
    "LG Energy Solution AMA",
    "text/plain"))
    .AllowAnonymous();
app.MapGet("/privacy", () => Results.Text(
    "The LG Energy Solution AMA processes conversation and tool content under your organization's Microsoft 365 and Microsoft Purview policies. Contact your tenant administrator for retention, access, and deletion requests.",
    "text/plain"))
    .AllowAnonymous();
app.MapGet("/terms", () => Results.Text(
    "Use of the LG Energy Solution AMA is subject to your organization's Microsoft 365 policies. Public company, news, IR and stock information is for a demonstration and is not investment advice or an official company statement. Verify source dates and originals.",
    "text/plain"))
    .AllowAnonymous();
readinessState.MarkReady(hasDurableStorage: false);
app.Run();

public partial class Program;
