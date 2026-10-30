using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Lges.Ama.AgentHost;
using System.IdentityModel.Tokens.Jwt;

namespace Lges.Ama.AgentHost.Tests;

[TestClass]
public sealed class AgentFrontendAuthorizationTests
{
    private const string BlueprintAudience = "11111111-1111-4111-8111-111111111111";
    private const string OtherAudience = "22222222-2222-4222-8222-222222222222";
    private const string TenantId = "33333333-3333-4333-8333-333333333333";

    [TestMethod]
    public async Task OboPolicyValidatesTheChannelAudience()
    {
        using var services = CreateServices(BlueprintAudience, OtherAudience);
        var jwtOptions = services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>();
        var policyProvider = services.GetRequiredService<IAuthorizationPolicyProvider>();

        var oboJwt = jwtOptions.Get(AgentFrontendAuthorization.OnBehalfOfScheme);
        var oboPolicy = await policyProvider.GetPolicyAsync(
            AgentFrontendAuthorization.OnBehalfOfPolicy);

        AssertStrictAudience(oboJwt, OtherAudience);
        CollectionAssert.AreEqual(
            new[] { AgentFrontendAuthorization.OnBehalfOfScheme },
            oboPolicy!.AuthenticationSchemes.ToArray());
        var schemes = await services.GetRequiredService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>().GetAllSchemesAsync();
        Assert.IsFalse(schemes.Any(s => s.Name == AgentFrontendAuthorization.AgenticUserScheme));
    }

    [TestMethod]
    public void InvalidFrontendAudienceIsRejected()
    {
        var configuration = CreateConfiguration(BlueprintAudience, string.Empty);
        var services = new ServiceCollection();

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            services.AddAgentAspNetAuthentication(configuration));
    }

    [TestMethod]
    public void MissingTenantIsRejected()
    {
        var configuration = CreateConfiguration(BlueprintAudience, OtherAudience);
        configuration["TokenValidation:TenantId"] = null;
        var services = new ServiceCollection();

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            services.AddAgentAspNetAuthentication(configuration));
    }

    [TestMethod]
    public void MalformedBearerTokenCannotSelectMetadataEndpoint()
    {
        Assert.IsFalse(AgentAuthenticationExtensions.TryReadIssuer(
            "not-a-jwt",
            out var issuer));
        Assert.AreEqual(string.Empty, issuer);
    }

    [TestMethod]
    public void ReadableBearerTokenSelectsItsIssuer()
    {
        const string expectedIssuer = "https://issuer.example";
        var token = new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityToken(issuer: expectedIssuer));

        Assert.IsTrue(AgentAuthenticationExtensions.TryReadIssuer(token, out var issuer));
        Assert.AreEqual(expectedIssuer, issuer);
    }

    private static ServiceProvider CreateServices(string agenticAudience, string oboAudience)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentAspNetAuthentication(
            CreateConfiguration(agenticAudience, oboAudience));
        services.AddAgentFrontendAuthorization();
        return services.BuildServiceProvider();
    }

    private static IConfiguration CreateConfiguration(string agenticAudience, string oboAudience) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TokenValidation:Enabled"] = "true",
                ["TokenValidation:TenantId"] = TenantId,
                ["TokenValidation:Audiences:AgenticUser"] = agenticAudience,
                ["TokenValidation:Audiences:OnBehalfOf"] = oboAudience,
                ["TokenValidation:ValidIssuers:0"] = "https://issuer.example",
                ["TokenValidation:OpenIdMetadataUrl"] = "https://issuer.example/.well-known/openid-configuration",
                ["TokenValidation:AzureBotServiceOpenIdMetadataUrl"] = "https://bot.example/.well-known/openid-configuration"
            })
            .Build();

    private static void AssertStrictAudience(JwtBearerOptions options, string audience)
    {
        var validation = options.TokenValidationParameters;
        Assert.AreEqual(audience, validation.ValidAudience);
        Assert.IsTrue(validation.ValidateIssuer);
        Assert.IsTrue(validation.ValidateAudience);
        Assert.IsTrue(validation.ValidateLifetime);
        Assert.IsTrue(validation.ValidateIssuerSigningKey);
        Assert.IsTrue(validation.RequireSignedTokens);
    }
}
