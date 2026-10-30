using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Options;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Lges.Ama.AgentHost;

namespace Lges.Ama.AgentHost.Tests;

[TestClass]
public sealed class AgentFrontendIdentityBindingTests
{
    private const string TenantId = "11111111-1111-4111-8111-111111111111";
    private const string AgenticAgentId = "22222222-2222-4222-8222-222222222222";
    private const string OboAgentId = "33333333-3333-4333-8333-333333333333";
    private const string OtherId = "44444444-4444-4444-8444-444444444444";
    private const string HumanUserId = "66666666-6666-4666-8666-666666666666";

    [TestMethod]
    public void AgenticIdentityUsesActivityChildAndConfiguredTenant()
    {
        var identity = CreateBinding().Resolve(
            CreateActivity(TenantId, AgenticAgentId),
            AgentFrontendMode.AgenticUser);

        Assert.AreEqual(AgenticAgentId, identity.AgentId);
        Assert.AreEqual(TenantId, identity.TenantId);
    }

    [TestMethod]
    public void AcceptsAnyValidAgenticChildUnderTheAuthenticatedBlueprint()
    {
        var identity = CreateBinding().Resolve(
            CreateActivity(TenantId, OtherId),
            AgentFrontendMode.AgenticUser);

        Assert.AreEqual(OtherId, identity.AgentId);
    }

    [TestMethod]
    public void RejectsActivityForDifferentTenant()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            CreateBinding().Resolve(
                CreateActivity(OtherId, AgenticAgentId),
                AgentFrontendMode.AgenticUser));
    }

    [TestMethod]
    public void OboIdentityUsesConfiguredTenantWhenActivityTenantIsMissing()
    {
        var identity = CreateBinding().Resolve(
            CreateActivity(null, null),
            AgentFrontendMode.OnBehalfOf);

        Assert.AreEqual(OboAgentId, identity.AgentId);
        Assert.AreEqual(TenantId, identity.TenantId);
    }

    [TestMethod]
    public void AgenticIdentityRejectsMissingActivityTenant()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            CreateBinding().Resolve(
                CreateActivity(null, AgenticAgentId),
                AgentFrontendMode.AgenticUser));
    }

    [TestMethod]
    public void OboIdentityRejectsExplicitDifferentActivityTenant()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            CreateBinding().Resolve(
                CreateActivity(OtherId, null),
                AgentFrontendMode.OnBehalfOf));
    }

    [TestMethod]
    public void RejectsInvalidOboChildSetting()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            CreateBinding("not-a-guid").Resolve(
                CreateActivity(TenantId, null),
                AgentFrontendMode.OnBehalfOf));
    }

    [TestMethod]
    public void ResourceTokenMustBelongToActivityChild()
    {
        var validToken = CreateToken(AgenticAgentId);
        var wrongToken = CreateToken(OtherId);

        AgentFrontendIdentityBinding.ValidateAgentToken(validToken, AgenticAgentId);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            AgentFrontendIdentityBinding.ValidateAgentToken(wrongToken, AgenticAgentId));
    }

    [TestMethod]
    public void UserAssertionSuppliesMissingDirectLineUserAndTenant()
    {
        var assertion = CreateUserAssertion(TenantId, HumanUserId);

        var userId = AgentFrontendIdentityBinding.ValidateUserAssertion(
            assertion,
            TenantId,
            activityUserId: null);

        Assert.AreEqual(Guid.Parse(HumanUserId), userId);
    }

    [TestMethod]
    public void UserAssertionRejectsDifferentTenant()
    {
        var assertion = CreateUserAssertion(OtherId, HumanUserId);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            AgentFrontendIdentityBinding.ValidateUserAssertion(
                assertion,
                TenantId,
                activityUserId: null));
    }

    [TestMethod]
    public void UserAssertionRejectsDifferentActivityUser()
    {
        var assertion = CreateUserAssertion(TenantId, HumanUserId);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            AgentFrontendIdentityBinding.ValidateUserAssertion(
                assertion,
                TenantId,
                OtherId));
    }

    [TestMethod]
    public async Task ConcurrentFrontendsRemainBoundToDistinctChildren()
    {
        var binding = CreateBinding();
        var agenticTask = Task.Run(() => binding.Resolve(
            CreateActivity(TenantId, AgenticAgentId),
            AgentFrontendMode.AgenticUser));
        var oboTask = Task.Run(() => binding.Resolve(
            CreateActivity(TenantId, null),
            AgentFrontendMode.OnBehalfOf));

        var identities = await Task.WhenAll(agenticTask, oboTask);

        Assert.AreEqual(AgenticAgentId, identities[0].AgentId);
        Assert.AreEqual(OboAgentId, identities[1].AgentId);
        Assert.AreNotEqual(identities[0].AgentId, identities[1].AgentId);
    }

    private static AgentFrontendIdentityBinding CreateBinding(string oboAgentId = OboAgentId) =>
        new(
            Options.Create(new AgentTokenValidationOptions
            {
                TenantId = TenantId,
                Audiences = new AgentFrontendAudienceOptions
                {
                    AgenticUser = "55555555-5555-4555-8555-555555555555",
                    OnBehalfOf = "55555555-5555-4555-8555-555555555555"
                }
            }),
            Options.Create(new AgentIdentityOboOptions
            {
                AgentId = oboAgentId,
                BlueprintConnectionName = "OboServiceConnection"
            }));

    private static Activity CreateActivity(string? tenantId, string? agenticAppId) =>
        new()
        {
            Recipient = new ChannelAccount
            {
                TenantId = tenantId,
                AgenticAppId = agenticAppId,
                Role = agenticAppId is null ? null : RoleTypes.AgenticUser
            },
            Conversation = new ConversationAccount { TenantId = tenantId }
        };

    private static string CreateToken(string clientId) =>
        new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityToken(claims: [new Claim("azp", clientId)]));

    private static string CreateUserAssertion(string tenantId, string userId) =>
        new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityToken(
                claims:
                [
                    new Claim("tid", tenantId),
                    new Claim("oid", userId)
                ]));
}
