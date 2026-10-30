using Lges.Ama.AgentHost;
using Microsoft.Agents.AI.Purview;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Options;

namespace Lges.Ama.AgentHost.Tests;

[TestClass]
public sealed class PurviewApplicationLocationResolverTests
{
    private const string TenantId = "11111111-1111-4111-8111-111111111111";
    private const string BlueprintId = "22222222-2222-4222-8222-222222222222";
    private const string OboAgentId = "33333333-3333-4333-8333-333333333333";
    private const string ChannelAppId = "44444444-4444-4444-8444-444444444444";
    private const string AgenticAgentId = "55555555-5555-4555-8555-555555555555";

    [TestMethod]
    public void ConfiguredBlueprintRemainsPurviewLocationDuringOboTurn()
    {
        var context = new AgentIdentityTokenContext();
        var tokenValidation = CreateTokenValidation();
        var obo = Options.Create(new AgentIdentityOboOptions { AgentId = OboAgentId });
        var purview = new PurviewDlpOptions { ApplicationId = BlueprintId };
        var identity = new AgentFrontendIdentityBinding(Options.Create(tokenValidation), obo)
            .Resolve(new Activity
            {
                ChannelId = Channels.Msteams,
                Conversation = new ConversationAccount { TenantId = TenantId }
            }, AgentFrontendMode.OnBehalfOf);
        using var turn = context.Push(new AgentIdentityTurnTokens(
            identity.AgentId, identity.TenantId, []));
        var resolver = new PurviewApplicationLocationResolver(
            context, obo, Options.Create(tokenValidation), Options.Create(purview));

        var settings = PurviewSettingsFactory.Create(purview, resolver.Resolve());

        Assert.AreEqual(OboAgentId, context.RequireCurrent().AgentId);
        Assert.AreEqual(BlueprintId, settings.PurviewAppLocation!.LocationValue);
        Assert.AreEqual(PurviewLocationType.Application, settings.PurviewAppLocation.LocationType);
        Assert.AreEqual(ChannelAppId, tokenValidation.GetAudience(AgentFrontendMode.OnBehalfOf));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    public void UnconfiguredLocationRetainsOboChannelFallback(string applicationId)
    {
        var context = new AgentIdentityTokenContext();
        using var turn = context.Push(new AgentIdentityTurnTokens(OboAgentId, TenantId, []));

        Assert.AreEqual(ChannelAppId, CreateResolver(context, applicationId).Resolve());
    }

    [TestMethod]
    public void UnconfiguredLocationRetainsAgenticAudienceFallback()
    {
        var context = new AgentIdentityTokenContext();
        using var turn = context.Push(new AgentIdentityTurnTokens(AgenticAgentId, TenantId, []));

        Assert.AreEqual(BlueprintId, CreateResolver(context, string.Empty).Resolve());
    }

    [TestMethod]
    public void ConfiguredLocationIsAvailableBeforeTurnAuthentication()
    {
        var context = new AgentIdentityTokenContext();

        Assert.AreEqual(BlueprintId, CreateResolver(context, BlueprintId).Resolve());
        Assert.AreEqual(string.Empty, CreateResolver(context, string.Empty).Resolve());
    }

    private static PurviewApplicationLocationResolver CreateResolver(
        AgentIdentityTokenContext context, string applicationId) => new(
            context,
            Options.Create(new AgentIdentityOboOptions { AgentId = OboAgentId }),
            Options.Create(CreateTokenValidation()),
            Options.Create(new PurviewDlpOptions { ApplicationId = applicationId }));

    private static AgentTokenValidationOptions CreateTokenValidation() => new()
    {
        TenantId = TenantId,
        Audiences = new AgentFrontendAudienceOptions
        {
            OnBehalfOf = ChannelAppId,
            AgenticUser = BlueprintId
        }
    };
}
