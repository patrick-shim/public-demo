using System.ComponentModel.DataAnnotations;

namespace Lges.Ama.AgentHost;

public sealed class AgentHostOptions
{
    public const string SectionName = "AgentHost";

    [Required]
    public string FoundryProjectEndpoint { get; init; } = string.Empty;

    [Required]
    public string FoundryModelDeployment { get; init; } = string.Empty;

    [Range(1, 100_000)]
    public int MaximumPromptCharacters { get; init; } = 32_768;
}
