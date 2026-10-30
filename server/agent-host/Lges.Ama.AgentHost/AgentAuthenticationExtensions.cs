using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.Agents.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Validators;

namespace Lges.Ama.AgentHost;

public static class AgentFrontendAuthorization
{
    public const string AgenticUserScheme = "AgenticUserJwt";
    public const string OnBehalfOfScheme = "OnBehalfOfJwt";
    public const string AgenticUserPolicy = "AgenticUserFrontend";
    public const string OnBehalfOfPolicy = "OnBehalfOfFrontend";
}

public sealed class AgentTokenValidationOptions
{
    public const string SectionName = "TokenValidation";

    public bool Enabled { get; init; } = true;

    public AgentFrontendAudienceOptions Audiences { get; init; } = new();

    public string? TenantId { get; init; }

    public List<string> ValidIssuers { get; init; } = [];

    public bool IsGov { get; init; }

    public string? AzureBotServiceOpenIdMetadataUrl { get; set; }

    public string? OpenIdMetadataUrl { get; set; }

    public bool AzureBotServiceTokenHandling { get; init; } = true;

    public TimeSpan? OpenIdMetadataRefresh { get; init; }

    public string GetAudience(AgentFrontendMode mode) =>
        mode switch
        {
            AgentFrontendMode.AgenticUser => Audiences.AgenticUser,
            AgentFrontendMode.OnBehalfOf => Audiences.OnBehalfOf,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported frontend mode.")
        };
}

public sealed class AgentFrontendAudienceOptions
{
    public string AgenticUser { get; init; } = string.Empty;

    public string OnBehalfOf { get; init; } = string.Empty;
}

public static class AgentAuthenticationExtensions
{
    private static readonly JwtSecurityTokenHandler TokenHandler = new();
    private static readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>>
        MetadataManagers = new(StringComparer.Ordinal);

    public static IServiceCollection AddAgentAspNetAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(AgentTokenValidationOptions.SectionName);
        services.AddOptions<AgentTokenValidationOptions>().Bind(section);
        if (!section.Exists() || !section.GetValue("Enabled", true))
        {
            services.AddAuthentication();
            return services;
        }

        var settings = section.Get<AgentTokenValidationOptions>()
            ?? throw new InvalidOperationException("TokenValidation configuration is invalid.");
        if (!Guid.TryParse(settings.TenantId, out _))
        {
            throw new InvalidOperationException(
                "TokenValidation:TenantId must contain the registered home tenant ID.");
        }

        if (!Guid.TryParse(settings.Audiences.OnBehalfOf, out _))
        {
            throw new InvalidOperationException(
                "TokenValidation:Audiences must contain a registered application ID for OnBehalfOf.");
        }

        ApplyDefaults(settings);
        var refreshInterval = settings.OpenIdMetadataRefresh
            ?? BaseConfigurationManager.DefaultAutomaticRefreshInterval;

        services.AddAuthentication()
            .AddJwtBearer(
                AgentFrontendAuthorization.OnBehalfOfScheme,
                options => ConfigureJwtBearer(
                    options,
                    settings,
                    settings.Audiences.OnBehalfOf,
                    refreshInterval));

        return services;
    }

    public static IServiceCollection AddAgentFrontendAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            options.AddPolicy(
                AgentFrontendAuthorization.AgenticUserPolicy,
                policy =>
                {
                    policy.AddAuthenticationSchemes(AgentFrontendAuthorization.AgenticUserScheme);
                    policy.RequireAuthenticatedUser();
                });
            options.AddPolicy(
                AgentFrontendAuthorization.OnBehalfOfPolicy,
                policy =>
                {
                    policy.AddAuthenticationSchemes(AgentFrontendAuthorization.OnBehalfOfScheme);
                    policy.RequireAuthenticatedUser();
                });
        });

        return services;
    }

    private static void ConfigureJwtBearer(
        JwtBearerOptions options,
        AgentTokenValidationOptions settings,
        string audience,
        TimeSpan refreshInterval)
    {
        options.SaveToken = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromMinutes(5),
            ValidAudience = audience,
            ValidIssuers = settings.ValidIssuers
        };
        options.TokenValidationParameters.EnableAadSigningKeyIssuerValidation();
        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                if (AgentIngressDiagnosticsMiddleware.IsObservedPath(context.Request.Path))
                {
                    var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("Lges.Ama.AgentHost.AgentJwtAuthentication");
                    AgentIngressDiagnosticsMiddleware.LogAuthenticationFailed(
                        logger,
                        AgentTurnDiagnostics.HashIdentifier(context.HttpContext.TraceIdentifier),
                        context.Exception.GetType().Name);
                }
                return Task.CompletedTask;
            },
            OnMessageReceived = context =>
            {
                var token = ReadBearerToken(context.Request.Headers.Authorization.ToString());
                if (token is null)
                {
                    return Task.CompletedTask;
                }

                if (!TryReadIssuer(token, out var issuer))
                {
                    context.Fail("The bearer token is malformed.");
                    return Task.CompletedTask;
                }

                var metadataUrl = settings.AzureBotServiceTokenHandling
                    && AuthenticationConstants.BotFrameworkTokenIssuer.Equals(
                        issuer,
                        StringComparison.Ordinal)
                        ? settings.AzureBotServiceOpenIdMetadataUrl
                        : settings.OpenIdMetadataUrl;

                context.Options.TokenValidationParameters.ConfigurationManager =
                    MetadataManagers.GetOrAdd(
                        metadataUrl!,
                        url => new ConfigurationManager<OpenIdConnectConfiguration>(
                            url,
                            new OpenIdConnectConfigurationRetriever(),
                            new HttpClient())
                        {
                            AutomaticRefreshInterval = refreshInterval
                        });

                return Task.CompletedTask;
            }
        };
    }

    private static string? ReadBearerToken(string authorizationHeader)
    {
        const string prefix = "Bearer ";
        return authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? authorizationHeader[prefix.Length..].Trim()
            : null;
    }

    internal static bool TryReadIssuer(string token, out string issuer)
    {
        issuer = string.Empty;
        if (string.IsNullOrWhiteSpace(token) || !TokenHandler.CanReadToken(token))
        {
            return false;
        }

        try
        {
            issuer = TokenHandler.ReadJwtToken(token).Issuer;
            return !string.IsNullOrWhiteSpace(issuer);
        }
        catch (ArgumentException)
        {
            issuer = string.Empty;
            return false;
        }
    }

    private static void ApplyDefaults(AgentTokenValidationOptions settings)
    {
        settings.AzureBotServiceOpenIdMetadataUrl ??=
            settings.IsGov
                ? AuthenticationConstants.GovAzureBotServiceOpenIdMetadataUrl
                : AuthenticationConstants.PublicAzureBotServiceOpenIdMetadataUrl;
        settings.OpenIdMetadataUrl ??=
            settings.IsGov
                ? AuthenticationConstants.GovOpenIdMetadataUrl
                : AuthenticationConstants.PublicOpenIdMetadataUrl;

        if (settings.ValidIssuers.Count > 0)
        {
            return;
        }

        settings.ValidIssuers.Add("https://api.botframework.com");
        if (Guid.TryParse(settings.TenantId, out _))
        {
            settings.ValidIssuers.Add(AuthenticationConstants.ValidTokenIssuerUrlTemplateV1.Replace(
                "{0}",
                settings.TenantId,
                StringComparison.Ordinal));
            settings.ValidIssuers.Add(AuthenticationConstants.ValidTokenIssuerUrlTemplateV2.Replace(
                "{0}",
                settings.TenantId,
                StringComparison.Ordinal));
        }
    }

}
