namespace Lges.Ama.AgentHost;

/// <summary>
/// A365 SDK configuration owned by the Agent 365 host boundary.
/// </summary>
public sealed class Agent365HostOptions
{
    public const string SectionName = "Agent365";

    /// <summary>
    /// WorkIQ remains disabled until Agent 365 Tooling is compatible with MCP 2.1 and generated
    /// tools satisfy equivalent tool-content protection.
    /// </summary>
    public bool EnableWorkIq { get; init; }
}
