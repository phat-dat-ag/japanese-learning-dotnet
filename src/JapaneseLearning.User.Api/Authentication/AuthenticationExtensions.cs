using System.Security.Claims;
using Microsoft.Extensions.Options;
using JapaneseLearning.User.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace JapaneseLearning.User.Api.Authentication;

public static class AuthenticationExtensions
{
    public static IServiceCollection AddJwtAuthentication(
        this IServiceCollection services)
    {
        services.AddHttpClient("jwt-jwks", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
            client.MaxResponseContentBufferSize = 1024 * 1024;
        });

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>, IHttpClientFactory>((options, configuredOptions, clients) =>
            {
                var jwtOptions = configuredOptions.Value;

                // Map sub, unique_name, email and role to the standard .NET claim types.
                options.MapInboundClaims = true;
                options.IncludeErrorDetails = false;

                // The service publishes a JWK Set, not an OIDC discovery document.
                // IdentityModel owns caching and throttled refresh on an unknown kid.
                options.RequireHttpsMetadata = jwtOptions.RequireHttpsMetadata;
                options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    jwtOptions.JwksUrl,
                    new JwksConfigurationRetriever(),
                    new HttpDocumentRetriever(clients.CreateClient("jwt-jwks"))
                    {
                        RequireHttps = jwtOptions.RequireHttpsMetadata
                    })
                {
                    AutomaticRefreshInterval = TimeSpan.FromMinutes(15),
                    RefreshInterval = TimeSpan.FromMinutes(1),
                    UseLastKnownGoodConfiguration = false
                };

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwtOptions.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwtOptions.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    TryAllIssuerSigningKeys = false,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    NameClaimType = ClaimTypes.Name,
                    RoleClaimType = ClaimTypes.Role,
                    ClockSkew = TimeSpan.Zero
                };
            });

        return services;
    }
}
