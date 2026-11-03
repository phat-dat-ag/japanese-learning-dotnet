using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using JapaneseLearning.User.Api.Authentication;
using JapaneseLearning.User.Api.Controllers;
using JapaneseLearning.User.Domain.Users;
using JapaneseLearning.User.Infrastructure.Configuration;
using JapaneseLearning.User.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace JapaneseLearning.User.UnitTests.Security;

public sealed class RetiredJwksEndpointTests(RsaKeyFixture fixture) : IClassFixture<RsaKeyFixture>
{
    [Fact]
    public async Task LegacyRouteIsAbsentWhileRemoteJwksAuthenticationStillWorks()
    {
        var options = new JwtOptions
        {
            Issuer = "JapaneseLearning.User",
            Audience = "JapaneseLearning",
            JwksUrl = fixture.JwksUrl,
            RequireHttpsMetadata = false,
            AccessTokenExpirationMinutes = 15,
            RefreshTokenExpirationDays = 7
        };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IOptions<JwtOptions>>(Options.Create(options));
        builder.Services.AddJwtAuthentication();
        builder.Services.AddAuthorization();
        builder.Services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly);
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        await using var app = builder.Build();
        app.UseSwagger();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapGet("/protected-test", () => "protected").RequireAuthorization();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)) };

        using var anonymousLegacy = await client.GetAsync("/.well-known/jwks.json");
        Assert.Equal(HttpStatusCode.NotFound, anonymousLegacy.StatusCode);
        using var anonymousProtected = await client.GetAsync("/protected-test");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousProtected.StatusCode);
        using var swagger = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        Assert.False(swagger.RootElement.GetProperty("paths").TryGetProperty("/.well-known/jwks.json", out _));

        var token = new TokenService(Options.Create(options), fixture.Keys)
            .CreateTokens(Guid.NewGuid(), "learner", "learner@example.com", UserRole.User).AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var authenticatedProtected = await client.GetAsync("/protected-test");
        Assert.Equal(HttpStatusCode.OK, authenticatedProtected.StatusCode);
        using var authenticatedLegacy = await client.GetAsync("/.well-known/jwks.json");
        Assert.Equal(HttpStatusCode.NotFound, authenticatedLegacy.StatusCode);
    }
}
