using Lges.Ama.Mcp;
using Lges.Ama.Mcp.Hosting;
using Lges.Ama.PublicData;

var builder = WebApplication.CreateBuilder(args);
var local = builder.Environment.IsDevelopment();
var profile = builder.Configuration["McpProfile"] ?? "news";
builder.Services.AddMcpWorkloadAuthorization(builder.Configuration, local);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient("PublicSources", client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
    .RemoveAllLoggers();
builder.Services.AddSingleton(services => new PublicHttp(services.GetRequiredService<IHttpClientFactory>().CreateClient("PublicSources"), TimeProvider.System));
builder.Services.AddSingleton<NewsProvider>();
builder.Services.AddSingleton<CompanyProvider>();
var mcp = builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true);
switch (profile)
{
    case "news": mcp.WithTools<NewsTools>(); break;
    case "ir": mcp.WithTools<IrTools>(); break;
    case "market": mcp.WithTools<MarketTools>(); break;
    default: throw new InvalidOperationException("McpProfile must be news, ir, or market.");
}
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapMcp("/mcp").RequireMcpWorkloadAuthorization(local);
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = profile }));
app.Run();

public partial class Program;
