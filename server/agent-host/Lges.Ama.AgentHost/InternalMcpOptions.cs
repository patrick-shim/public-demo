using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Lges.Ama.AgentHost;

public sealed class InternalMcpOptions
{
    public const string SectionName = "InternalMcp";
    public bool Enabled { get; init; }
    public bool UseAuthentication { get; init; } = true;
    public string Audience { get; init; } = string.Empty;
    public string NewsEndpoint { get; init; } = string.Empty;
    public string IrEndpoint { get; init; } = string.Empty;
    public string MarketEndpoint { get; init; } = string.Empty;
    [Range(1024, 262144)] public int MaximumContentCharacters { get; init; } = 65536;
    public bool HasValidEndpoints(bool requireHttps = false) => Endpoints().All(value =>
        Uri.TryCreate(value.Endpoint, UriKind.Absolute, out var uri) && string.IsNullOrEmpty(uri.UserInfo)
        && (requireHttps ? uri.Scheme == "https" : uri.Scheme is "http" or "https"));
    public bool HasValidAudience() => !UseAuthentication || (Uri.TryCreate(Audience, UriKind.Absolute, out var uri) && uri.Scheme == "api");
    public string GetDelegatedScope() => AgentIdentityAuthorizationScopes.InternalMcp(Audience);
    public IReadOnlyList<InternalMcpServer> GetServers()
    {
        using var stream = typeof(InternalMcpOptions).Assembly.GetManifestResourceStream("Lges.Ama.McpContracts.json")
            ?? throw new InvalidOperationException("MCP contracts are missing.");
        var contracts = JsonSerializer.Deserialize<Dictionary<string, InternalMcpToolContract[]>>(stream)
            ?? throw new InvalidDataException("MCP contracts are invalid.");
        return Endpoints().Select(e => new InternalMcpServer(e.Name, new Uri(e.Endpoint), contracts[e.Name])).ToArray();
    }
    private (string Name, string Endpoint)[] Endpoints() => [("news", NewsEndpoint), ("ir", IrEndpoint), ("market", MarketEndpoint)];
}
public sealed record InternalMcpServer(string Name, Uri Endpoint, IReadOnlyList<InternalMcpToolContract> ToolContracts);
public sealed record InternalMcpToolContract(string Name, string Description, IReadOnlyList<string> Parameters, IReadOnlyList<string> RequiredParameters, string SchemaSha256);
