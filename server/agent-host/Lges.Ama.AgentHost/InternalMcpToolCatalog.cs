using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace Lges.Ama.AgentHost;

public sealed class InternalMcpToolCatalog(
    IHttpClientFactory httpClientFactory,
    IOptions<InternalMcpOptions> options,
    ILoggerFactory loggerFactory)
{
    private readonly InternalMcpOptions _options = options.Value;

    public async Task<InternalMcpToolSession> OpenAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return InternalMcpToolSession.Empty;
        }

        var clients = new List<McpClient>();
        var toolsByName = new Dictionary<string, AITool>(StringComparer.Ordinal);
        try
        {
            foreach (var server in _options.GetServers())
            {
                var transport = new HttpClientTransport(
                    new HttpClientTransportOptions
                    {
                        Endpoint = server.Endpoint,
                        Name = server.Name,
                        TransportMode = HttpTransportMode.StreamableHttp,
                        EnableStandaloneGetStream = false
                    },
                    httpClientFactory.CreateClient("InternalMcp"),
                    loggerFactory,
                    ownsHttpClient: true);
                var client = await McpClient.CreateAsync(
                    transport,
                    loggerFactory: loggerFactory,
                    cancellationToken: cancellationToken);
                clients.Add(client);

                var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
                ValidateToolContracts(server, tools);
                foreach (var tool in tools)
                {
                    if (!toolsByName.TryAdd(tool.Name, tool))
                    {
                        throw new InvalidOperationException(
                            $"Internal MCP tool name '{tool.Name}' is duplicated across configured servers.");
                    }
                }
            }

            return new InternalMcpToolSession(clients, [.. toolsByName.Values]);
        }
        catch
        {
            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }

            throw;
        }
    }

    private static void ValidateToolContracts(
        InternalMcpServer server,
        IList<McpClientTool> tools)
    {
        var toolsByName = tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        var expectedNames = server.ToolContracts.Select(contract => contract.Name).ToHashSet(StringComparer.Ordinal);
        var actualNames = toolsByName.Keys.ToHashSet(StringComparer.Ordinal);
        if (!actualNames.SetEquals(expectedNames))
        {
            throw new InvalidOperationException(
                $"Internal MCP server '{server.Name}' advertised an unexpected tool set.");
        }

        foreach (var contract in server.ToolContracts)
        {
            var tool = toolsByName[contract.Name];
            if (!string.Equals(tool.ProtocolTool.Description, contract.Description, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Internal MCP tool '{contract.Name}' advertised an unexpected description.");
            }

            var schema = tool.ProtocolTool.InputSchema;
            if (schema.ValueKind != System.Text.Json.JsonValueKind.Object
                || !schema.TryGetProperty("properties", out var properties)
                || properties.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"Internal MCP tool '{contract.Name}' advertised an invalid input schema.");
            }

            var parameters = properties
                .EnumerateObject()
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);
            var requiredParameters = schema.TryGetProperty("required", out var required)
                && required.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? required.EnumerateArray()
                        .Select(item => item.GetString() ?? string.Empty)
                        .ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);

            if (!parameters.SetEquals(contract.Parameters)
                || !requiredParameters.SetEquals(contract.RequiredParameters))
            {
                throw new InvalidOperationException(
                    $"Internal MCP tool '{contract.Name}' advertised an unexpected input schema.");
            }

            var schemaFingerprint = ComputeSchemaFingerprint(schema);
            if (!string.Equals(schemaFingerprint, contract.SchemaSha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Internal MCP tool '{contract.Name}' advertised an altered input schema.");
            }
        }
    }

    private static string ComputeSchemaFingerprint(JsonElement schema)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            WriteCanonical(writer, schema);
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element
                    .EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}

public sealed class InternalMcpToolSession : IAsyncDisposable
{
    private readonly IReadOnlyList<McpClient> _clients;

    internal InternalMcpToolSession(
        IReadOnlyList<McpClient> clients,
        IReadOnlyList<AITool> tools)
    {
        _clients = clients;
        Tools = tools;
    }

    internal static InternalMcpToolSession Empty { get; } = new([], []);

    public IReadOnlyList<AITool> Tools { get; }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            await client.DisposeAsync();
        }
    }
}
