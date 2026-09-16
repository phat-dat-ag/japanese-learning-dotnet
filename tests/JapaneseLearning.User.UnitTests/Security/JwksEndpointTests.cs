using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using JapaneseLearning.User.Api.Authentication;
using JapaneseLearning.User.Api.Controllers;
using JapaneseLearning.User.Application.Abstractions.Security;
using JapaneseLearning.User.Domain.Users;
using JapaneseLearning.User.Infrastructure;
using JapaneseLearning.User.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace JapaneseLearning.User.UnitTests.Security;

public sealed class JwksEndpointTests(RsaKeyFixture fixture) : IClassFixture<RsaKeyFixture>
{
    [Theory]
    [InlineData("japanese-learning-local-1")]
    [InlineData("another-configured-key-id")]
    public async Task AnonymousEndpointPublishesOnlyConfiguredPublicKeyAndValidatesToken(string keyId)
    {
        await using var app = CreateApplication(keyId);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)) };
            // Prove the fallback policy is active while JWKS explicitly permits anonymous access.
            using var protectedResponse = await client.GetAsync("/protected-test");
            Assert.Equal(HttpStatusCode.Unauthorized, protectedResponse.StatusCode);
            Assert.Null(client.DefaultRequestHeaders.Authorization);
            using var response = await client.GetAsync("/.well-known/jwks.json");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("keys", Assert.Single(document.RootElement.EnumerateObject()).Name);
            var keys = document.RootElement.GetProperty("keys");
            Assert.Equal(JsonValueKind.Array, keys.ValueKind);
            var key = Assert.Single(keys.EnumerateArray());

            // Exact field allowlist also excludes d, p, q, dp, dq, qi, oth and internal metadata.
            Assert.Equal(new[] { "alg", "e", "kid", "kty", "n", "use" },
                key.EnumerateObject().Select(property => property.Name).OrderBy(name => name));
            Assert.Equal("RSA", key.GetProperty("kty").GetString());
            Assert.Equal("sig", key.GetProperty("use").GetString());
            Assert.Equal("RS256", key.GetProperty("alg").GetString());
            Assert.Equal(keyId, key.GetProperty("kid").GetString());
            var modulus = key.GetProperty("n").GetString()!;
            var exponent = key.GetProperty("e").GetString()!;
            Assert.Matches("^[A-Za-z0-9_-]+$", modulus);
            Assert.Matches("^[A-Za-z0-9_-]+$", exponent);
            var loadedPublicKey = app.Services.GetRequiredService<JwtRsaKeys>().ValidationKey;
            Assert.Equal(loadedPublicKey.Parameters.Modulus, Base64UrlEncoder.DecodeBytes(modulus));
            Assert.Equal(loadedPublicKey.Parameters.Exponent, Base64UrlEncoder.DecodeBytes(exponent));

            var userId = Guid.NewGuid();
            var token = app.Services.GetRequiredService<ITokenService>()
                .CreateTokens(userId, "learner", "learner@example.com", UserRole.User).AccessToken;
            var handler = new JwtSecurityTokenHandler();
            Assert.Equal(key.GetProperty("kid").GetString(), handler.ReadJwtToken(token).Header.Kid);

            // A resource server needs only the published n/e, plus its expected issuer/audience.
            var publicParameters = new RSAParameters
            {
                Modulus = Base64UrlEncoder.DecodeBytes(modulus),
                Exponent = Base64UrlEncoder.DecodeBytes(exponent)
            };
            var parameters = new TokenValidationParameters
            {
                IssuerSigningKey = new RsaSecurityKey(publicParameters) { KeyId = keyId },
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ValidateIssuer = true,
                ValidIssuer = "JapaneseLearning.User",
                ValidateAudience = true,
                ValidAudience = "JapaneseLearning",
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.Zero
            };
            var principal = handler.ValidateToken(token, parameters, out var validatedToken);
            Assert.Equal(userId.ToString(), Assert.IsType<JwtSecurityToken>(validatedToken).Subject);
            Assert.True(principal.IsInRole("User"));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private WebApplication CreateApplication(string keyId)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = fixture.DirectoryPath,
            EnvironmentName = "Testing"
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = "Server=unused;Database=unused",
            ["Jwt:PrivateKeyPath"] = fixture.PrivateKeyPath,
            ["Jwt:PublicKeyPath"] = fixture.PublicKeyPath,
            ["Jwt:KeyId"] = keyId,
            ["Jwt:Issuer"] = "JapaneseLearning.User",
            ["Jwt:Audience"] = "JapaneseLearning",
            ["Jwt:AccessTokenExpirationMinutes"] = "15",
            ["Jwt:RefreshTokenExpirationDays"] = "7"
        });
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddJwtAuthentication();
        builder.Services.AddAuthorization(options => options.FallbackPolicy =
            new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddControllers().AddApplicationPart(typeof(JwksController).Assembly);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapGet("/protected-test", () => "protected");
        return app;
    }
}
