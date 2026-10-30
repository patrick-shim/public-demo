using System.Reflection;
using System.Collections;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Agents.AI.Purview;

namespace Lges.Ama.AgentHost;

public static class PurviewSerializationWorkaround
{
    private const string ActivityTypeName =
        "Microsoft.Agents.AI.Purview.Models.Common.Activity";
    private const string ActivityMetadataTypeName =
        "Microsoft.Agents.AI.Purview.Models.Common.ActivityMetadata";
    private const string AgentInfoTypeName =
        "Microsoft.Agents.AI.Purview.Models.Common.AIAgentInfo";
    private const string ConversationMetadataTypeName =
        "Microsoft.Agents.AI.Purview.Models.Common.ProcessConversationMetadata";
    private const string SerializationUtilsTypeName =
        "Microsoft.Agents.AI.Purview.Serialization.PurviewSerializationUtils";
    private static readonly AsyncLocal<AgentMetadataState?> CurrentMetadata = new();

    public static void Apply()
    {
        var packageAssembly = typeof(PurviewSettings).Assembly;
        var serializationUtils = packageAssembly.GetType(SerializationUtilsTypeName)
            ?? throw new InvalidOperationException("The Purview serializer utility was not found.");
        var settingsProperty = serializationUtils.GetProperty(
            "SerializationSettings",
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("The Purview serializer settings were not found.");
        var options = settingsProperty.GetValue(null) as JsonSerializerOptions
            ?? throw new InvalidOperationException("The Purview serializer settings are invalid.");

        lock (options)
        {
            if (options.TypeInfoResolver is PurviewTypeInfoResolver)
            {
                return;
            }

            if (options.IsReadOnly)
            {
                throw new InvalidOperationException(
                    "The Purview serializer was used before its activity wire format was corrected.");
            }

            options.TypeInfoResolver = new PurviewTypeInfoResolver(
                options.TypeInfoResolver
                    ?? throw new InvalidOperationException("The Purview type resolver was not found."));
        }
    }

    public static IDisposable PushAgentMetadata(
        string blueprintId,
        string agentId,
        string agentName,
        string agentVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blueprintId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentVersion);

        var prior = CurrentMetadata.Value;
        var current = new AgentMetadataState(blueprintId, agentId, agentName, agentVersion);
        CurrentMetadata.Value = current;
        return new AgentMetadataScope(current, prior);
    }

    private sealed class PurviewTypeInfoResolver(IJsonTypeInfoResolver innerResolver)
        : IJsonTypeInfoResolver
    {
        private static readonly MethodInfo CreateValueInfoMethod = typeof(JsonMetadataServices)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == nameof(JsonMetadataServices.CreateValueInfo)
                && method.IsGenericMethodDefinition);

        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            if (string.Equals(type.FullName, ActivityTypeName, StringComparison.Ordinal))
            {
                return CreateValueInfoMethod
                    .MakeGenericMethod(type)
                    .Invoke(null, [options, CreateActivityConverter(type)]) as JsonTypeInfo
                    ?? throw new InvalidOperationException("The Purview activity type metadata could not be created.");
            }

            var typeInfo = innerResolver.GetTypeInfo(type, options);
            if (typeInfo is null)
            {
                return null;
            }

            if (string.Equals(type.FullName, ActivityMetadataTypeName, StringComparison.Ordinal))
            {
                var activityProperty = typeInfo.Properties.Single(property => property.Name == "activity");
                activityProperty.CustomConverter = CreateActivityConverter(activityProperty.PropertyType);
            }
            else if (string.Equals(type.FullName, AgentInfoTypeName, StringComparison.Ordinal))
            {
                var odataTypeProperty = typeInfo.CreateJsonPropertyInfo(typeof(string), "@odata.type");
                odataTypeProperty.Get = _ => "microsoft.graph.aiAgentInfo";
                typeInfo.Properties.Add(odataTypeProperty);

                var blueprintProperty = typeInfo.CreateJsonPropertyInfo(typeof(string), "blueprintId");
                blueprintProperty.Get = _ => CurrentMetadata.Value?.BlueprintId;
                blueprintProperty.ShouldSerialize = (_, value) => value is string;
                typeInfo.Properties.Add(blueprintProperty);
            }
            else if (string.Equals(type.FullName, ConversationMetadataTypeName, StringComparison.Ordinal))
            {
                var correlationProperty = typeInfo.Properties.Single(
                    property => property.Name == "correlationId");
                var getCorrelationId = correlationProperty.Get
                    ?? throw new InvalidOperationException(
                        "The Purview correlation ID accessor was not found.");
                correlationProperty.Get = value => NormalizeCorrelationId(
                    getCorrelationId(value)?.ToString());

                var agentsProperty = typeInfo.Properties.Single(property => property.Name == "agents");
                agentsProperty.Get = _ => CreateAgentList(type.Assembly);
                agentsProperty.ShouldSerialize = (_, value) => value is not null;
            }

            return typeInfo;
        }

        private static string NormalizeCorrelationId(string? value)
        {
            var candidate = value?.EndsWith("@AF", StringComparison.Ordinal) == true
                ? value[..^3]
                : value;
            if (Guid.TryParse(candidate, out var parsed))
            {
                return parsed.ToString("D");
            }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
            return new Guid(hash.AsSpan(0, 16)).ToString("D");
        }

        private static JsonConverter CreateActivityConverter(Type activityType)
        {
            var converterType = typeof(LowerCamelEnumConverter<>).MakeGenericType(activityType);
            return Activator.CreateInstance(converterType) as JsonConverter
                ?? throw new InvalidOperationException("The Purview activity converter could not be created.");
        }

        private static object? CreateAgentList(Assembly packageAssembly)
        {
            var metadata = CurrentMetadata.Value;
            if (metadata is null)
            {
                return null;
            }

            var agentInfoType = packageAssembly.GetType(AgentInfoTypeName)
                ?? throw new InvalidOperationException("The Purview agent metadata type was not found.");
            var agentInfo = Activator.CreateInstance(agentInfoType)
                ?? throw new InvalidOperationException("The Purview agent metadata could not be created.");
            agentInfoType.GetProperty("Identifier")!.SetValue(agentInfo, metadata.AgentId);
            agentInfoType.GetProperty("Name")!.SetValue(agentInfo, metadata.AgentName);
            agentInfoType.GetProperty("Version")!.SetValue(agentInfo, metadata.AgentVersion);

            var list = Activator.CreateInstance(typeof(List<>).MakeGenericType(agentInfoType)) as IList
                ?? throw new InvalidOperationException("The Purview agent metadata list could not be created.");
            list.Add(agentInfo);
            return list;
        }
    }

    private sealed class LowerCamelEnumConverter<TEnum> : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        public override TEnum Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            var value = reader.GetString();
            return Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
                ? parsed
                : throw new JsonException($"Unknown {typeof(TEnum).Name} value.");
        }

        public override void Write(
            Utf8JsonWriter writer,
            TEnum value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
    }

    private sealed class AgentMetadataScope(
        AgentMetadataState current,
        AgentMetadataState? prior) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (!ReferenceEquals(CurrentMetadata.Value, current))
            {
                throw new InvalidOperationException(
                    "Purview agent metadata scopes must be disposed in creation order.");
            }

            CurrentMetadata.Value = prior;
            _disposed = true;
        }
    }

    private sealed record AgentMetadataState(
        string BlueprintId,
        string AgentId,
        string AgentName,
        string AgentVersion);
}
