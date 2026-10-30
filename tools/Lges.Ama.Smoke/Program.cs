using System.ClientModel.Primitives;
using Azure.Identity;
using Lges.Ama.Agent;
using Lges.Ama.AgentHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Responses;

var services = new ServiceCollection();
services.AddHttpClient("InternalMcp");
await using var provider = services.BuildServiceProvider();
var catalog = new InternalMcpToolCatalog(provider.GetRequiredService<IHttpClientFactory>(), Options.Create(new InternalMcpOptions
{
    Enabled = true, UseAuthentication = false,
    NewsEndpoint = "http://127.0.0.1:5211/mcp", IrEndpoint = "http://127.0.0.1:5212/mcp", MarketEndpoint = "http://127.0.0.1:5213/mcp"
}), NullLoggerFactory.Instance);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
await using var session = await catalog.OpenAsync(timeout.Token);
Console.WriteLine($"PASS: exact MCP tool names, descriptions and schemas verified ({session.Tools.Count} tools).");
if (!args.Contains("--model", StringComparer.Ordinal)) return;

// Operator credential is used only by this explicit local smoke check. This does not prove channel OBO.
static string RequiredEnvironment(string name) => Environment.GetEnvironmentVariable(name)
    is { Length: > 0 } value ? value : throw new InvalidOperationException($"Set {name} before running --model.");
var credential = new AzureCliCredential(new AzureCliCredentialOptions { TenantId = RequiredEnvironment("AZURE_TENANT_ID") });
var endpoint = RequiredEnvironment("AgentHost__FoundryProjectEndpoint").TrimEnd('/');
var deployment = RequiredEnvironment("AgentHost__FoundryModelDeployment");
#pragma warning disable OPENAI001, MAAI001
using var chat = new OpenAIClient(new BearerTokenPolicy(credential, "https://ai.azure.com/.default"),
    new OpenAIClientOptions { Endpoint = new Uri($"{endpoint}/openai/v1") })
    .GetResponsesClient().AsIChatClientWithStoredOutputDisabled(deployment).AsBuilder().UseFunctionInvocation().Build();
#pragma warning restore OPENAI001, MAAI001
var agent = CompanyAgentFactory.Create(chat, session.Tools);
var prompt = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
    ?? "LG에너지솔루션의 주가를 도구로 조회하고 가격, 통화, 기준 시각과 출처를 한국어로 알려 줘.";
var response = await agent.RunAsync(prompt, cancellationToken: timeout.Token);
Console.WriteLine(response.Text);
