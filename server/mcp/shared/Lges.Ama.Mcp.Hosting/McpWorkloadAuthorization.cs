using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Lges.Ama.Mcp.Hosting;

public sealed class McpAuthorizationOptions
{
    public const string SectionName = "McpAuthorization";

    public string TenantId { get; init; } = string.Empty;

    public string Audience { get; init; } = string.Empty;

    public string RequiredScope { get; init; } = McpWorkloadAuthorization.DefaultScope;

    public bool IsValid() =>
        Guid.TryParse(TenantId, out _)
        && Guid.TryParse(Audience, out _)
        && !string.IsNullOrWhiteSpace(RequiredScope);
}

public static class McpWorkloadAuthorization
{
    public const string PolicyName = "McpWorkload";
    public const string DefaultScope = "Mcp.Invoke";

    public static IServiceCollection AddMcpWorkloadAuthorization(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isLocalEnvironment)
    {
        var optionsBuilder = services
            .AddOptions<McpAuthorizationOptions>()
            .Bind(configuration.GetSection(McpAuthorizationOptions.SectionName));

        if (isLocalEnvironment)
        {
            services.AddAuthentication();
            services.AddAuthorization();
            return services;
        }

        optionsBuilder
            .Validate(options => options.IsValid(),
                "McpAuthorization requires valid tenant and v2 API application IDs plus a delegated scope.")
            .ValidateOnStart();

        var settings = configuration
            .GetSection(McpAuthorizationOptions.SectionName)
            .Get<McpAuthorizationOptions>() ?? new McpAuthorizationOptions();
        var issuer = $"https://login.microsoftonline.com/{settings.TenantId}/v2.0";

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = issuer;
                options.Audience = settings.Audience;
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = settings.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true
                };
            });
        services.AddAuthorization(options => options.AddPolicy(
            PolicyName,
            policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim("tid", settings.TenantId)
                .RequireAssertion(context => HasScope(context.User, settings.RequiredScope))));
        return services;
    }

    private static bool HasScope(System.Security.Claims.ClaimsPrincipal principal, string requiredScope) =>
        principal
            .FindAll("scp")
            .SelectMany(claim => claim.Value.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Contains(requiredScope, StringComparer.Ordinal);

    public static TBuilder RequireMcpWorkloadAuthorization<TBuilder>(
        this TBuilder endpoint,
        bool isLocalEnvironment)
        where TBuilder : IEndpointConventionBuilder
    {
        if (isLocalEnvironment)
        {
            endpoint.AllowAnonymous();
        }
        else
        {
            endpoint.RequireAuthorization(PolicyName);
        }

        return endpoint;
    }
}
