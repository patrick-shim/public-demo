using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using Microsoft.Agents.A365.Runtime.Utils;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;

namespace Lges.Ama.AgentHost.Tests;

[TestClass]
public sealed class AgentIdentityResolutionTests
{
    private const string ChildAgentId = "33333333-3333-4333-8333-333333333333";
    private const string HumanUserId = "44444444-4444-4444-8444-444444444444";

    [TestMethod]
    public void AgenticTenantUsesRecipientBeforeConversation()
    {
        const string recipientTenantId = "11111111-1111-4111-8111-111111111111";
        var activity = new Activity
        {
            Recipient = new ChannelAccount { TenantId = recipientTenantId },
            Conversation = new ConversationAccount
            {
                TenantId = "22222222-2222-4222-8222-222222222222"
            }
        };

        Assert.AreEqual(recipientTenantId, activity.GetAgenticTenantId());
    }

    [TestMethod]
    public void OboIdentityUsesChildAgentTokenClaimInsteadOfHumanUserId()
    {
        var activity = new Activity
        {
            From = new ChannelAccount { AadObjectId = HumanUserId }
        };
        var turnContext = DispatchProxy.Create<ITurnContext, TurnContextProxy>();
        ((TurnContextProxy)(object)turnContext).Activity = activity;
        var token = new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityToken(claims: [new Claim("azp", ChildAgentId)]));

        var resolvedAgentId = Utility.ResolveAgentIdentity(turnContext, token);

        Assert.AreEqual(ChildAgentId, resolvedAgentId);
        Assert.AreEqual(HumanUserId, turnContext.Activity.From?.AadObjectId);
    }

    public class TurnContextProxy : DispatchProxy
    {
        public IActivity Activity { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "get_Activity" => Activity,
                "get_Identity" => new ClaimsIdentity(),
                _ => throw new NotSupportedException(
                    $"Turn context member '{targetMethod?.Name}' is not used by this test.")
            };
    }
}
