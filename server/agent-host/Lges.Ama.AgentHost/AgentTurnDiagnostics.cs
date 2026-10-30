using System.Security.Cryptography;
using System.Text;
using Microsoft.Agents.Core.Models;

namespace Lges.Ama.AgentHost;

internal static class AgentTurnDiagnostics
{
    public static IDisposable? BeginScope(
        ILogger logger,
        AgentFrontendMode frontendMode,
        IActivity activity) =>
        logger.BeginScope(CreateScopeValues(frontendMode, activity));

    internal static IReadOnlyDictionary<string, object> CreateScopeValues(
        AgentFrontendMode frontendMode,
        IActivity activity) =>
        new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["TraceId"] = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? string.Empty,
            ["FrontendMode"] = frontendMode.ToString(),
            ["ConversationHash"] = HashIdentifier(activity.Conversation?.Id),
            ["ActivityHash"] = HashIdentifier(activity.Id)
        };

    internal static string HashIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest.AsSpan(0, 12));
    }
}
