using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Lges.Ama.Agent;

public static class CompanyAgentFactory
{
    public static AIAgent Create(
        IChatClient chatClient,
        IEnumerable<AITool> tools,
        string? agentId = null)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(tools);

        var options = new ChatClientAgentOptions
        {
            Name = "LG Energy Solution AMA",
            ChatOptions = new ChatOptions
            {
                Instructions = CompanyAgentInstructions.Create(),
                Tools = [.. tools]
            }
        };

        if (!string.IsNullOrWhiteSpace(agentId))
        {
            options.Id = agentId;
        }

        return new ChatClientAgent(chatClient, options);
    }
}
