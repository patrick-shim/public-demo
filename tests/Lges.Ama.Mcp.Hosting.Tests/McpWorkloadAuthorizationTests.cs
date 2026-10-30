using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Lges.Ama.Mcp.Hosting;

namespace Lges.Ama.Mcp.Hosting.Tests;

[TestClass]
public sealed class McpWorkloadAuthorizationTests
{
    private const string TenantId = "11111111-1111-4111-8111-111111111111";
    private const string ApiApplicationId = "33333333-3333-4333-8333-333333333333";
    private const string OtherApplicationId = "44444444-4444-4444-8444-444444444444";
    private static readonly SymmetricSecurityKey SigningKey = new(
        Encoding.UTF8.GetBytes("korea-expert-local-jwt-signing-key"));

    [TestMethod]
    public async Task PolicyAcceptsConfiguredTenantAndSpaceDelimitedScope()
    {
        await using var provider = CreateProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var principal = CreatePrincipal(TenantId, $"openid {McpWorkloadAuthorization.DefaultScope} profile");

        var result = await authorization.AuthorizeAsync(
            principal,
            resource: null,
            McpWorkloadAuthorization.PolicyName);

        Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    public async Task PolicyRejectsWrongTenantAndMissingOrWrongScope()
    {
        await using var provider = CreateProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();

        var wrongTenant = await authorization.AuthorizeAsync(
            CreatePrincipal("22222222-2222-4222-8222-222222222222", McpWorkloadAuthorization.DefaultScope),
            resource: null,
            McpWorkloadAuthorization.PolicyName);
        var missingTenant = await authorization.AuthorizeAsync(
            CreatePrincipal(tenantId: null, McpWorkloadAuthorization.DefaultScope),
            resource: null,
            McpWorkloadAuthorization.PolicyName);
        var missingScope = await authorization.AuthorizeAsync(
            CreatePrincipal(TenantId, scope: null),
            resource: null,
            McpWorkloadAuthorization.PolicyName);
        var wrongScope = await authorization.AuthorizeAsync(
            CreatePrincipal(TenantId, "Mcp.Read"),
            resource: null,
            McpWorkloadAuthorization.PolicyName);

        Assert.IsFalse(wrongTenant.Succeeded);
        Assert.IsFalse(missingTenant.Succeeded);
        Assert.IsFalse(missingScope.Succeeded);
        Assert.IsFalse(wrongScope.Succeeded);
    }

    [TestMethod]
    public void ProductionConfigurationRejectsMissingAudience()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpWorkloadAuthorization(
            CreateConfiguration(new Dictionary<string, string?>
            {
                ["McpAuthorization:TenantId"] = TenantId,
                ["McpAuthorization:Audience"] = ""
            }),
            isLocalEnvironment: false);
        using var provider = services.BuildServiceProvider();

        Assert.ThrowsExactly<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<McpAuthorizationOptions>>().Value);
    }

    [TestMethod]
    public void ProductionJwtValidationUsesV2ApplicationIdAudience()
    {
        using var provider = CreateProvider();
        var jwt = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.AreEqual(ApiApplicationId, jwt.Audience);
        Assert.AreEqual(ApiApplicationId, jwt.TokenValidationParameters.ValidAudience);
        Assert.AreEqual(
            $"https://login.microsoftonline.com/{TenantId}/v2.0",
            jwt.TokenValidationParameters.ValidIssuer);
    }

    [TestMethod]
    public void LocallySignedTokenAcceptsApplicationIdAudience()
    {
        using var provider = CreateProvider();
        var parameters = GetLocalValidationParameters(provider);

        var principal = CreateTokenHandler().ValidateToken(
            CreateSignedToken(ApiApplicationId),
            parameters,
            out _);

        Assert.AreEqual(TenantId, principal.FindFirst("tid")?.Value);
    }

    [TestMethod]
    [DataRow("api://33333333-3333-4333-8333-333333333333")]
    [DataRow(OtherApplicationId)]
    public void LocallySignedTokenRejectsNonApplicationIdAudience(string audience)
    {
        using var provider = CreateProvider();
        var parameters = GetLocalValidationParameters(provider);

        Assert.ThrowsExactly<SecurityTokenInvalidAudienceException>(() =>
            CreateTokenHandler().ValidateToken(
                CreateSignedToken(audience),
                parameters,
                out _));
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpWorkloadAuthorization(
            CreateConfiguration(new Dictionary<string, string?>
            {
                ["McpAuthorization:TenantId"] = TenantId,
                ["McpAuthorization:Audience"] = ApiApplicationId,
                ["McpAuthorization:RequiredScope"] = McpWorkloadAuthorization.DefaultScope
            }),
            isLocalEnvironment: false);
        return services.BuildServiceProvider();
    }

    private static IConfiguration CreateConfiguration(
        IDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static TokenValidationParameters GetLocalValidationParameters(
        IServiceProvider provider)
    {
        var configured = provider
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme)
            .TokenValidationParameters;
        var local = configured.Clone();
        local.IssuerSigningKey = SigningKey;
        return local;
    }

    private static string CreateSignedToken(string audience)
    {
        var issuer = $"https://login.microsoftonline.com/{TenantId}/v2.0";
        var token = new JwtSecurityToken(
            issuer,
            audience,
            [
                new Claim("tid", TenantId),
                new Claim("scp", McpWorkloadAuthorization.DefaultScope)
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static JwtSecurityTokenHandler CreateTokenHandler() =>
        new() { MapInboundClaims = false };

    private static ClaimsPrincipal CreatePrincipal(string? tenantId, string? scope)
    {
        var claims = new List<Claim>();
        if (tenantId is not null)
        {
            claims.Add(new Claim("tid", tenantId));
        }

        if (scope is not null)
        {
            claims.Add(new Claim("scp", scope));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }
}
