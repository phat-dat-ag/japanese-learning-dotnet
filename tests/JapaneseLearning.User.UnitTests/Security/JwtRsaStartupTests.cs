using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using JapaneseLearning.User.Api.Authentication;
using JapaneseLearning.User.Application.Abstractions.Security;
using JapaneseLearning.User.Domain.Users;
using JapaneseLearning.User.Infrastructure;
using JapaneseLearning.User.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace JapaneseLearning.User.UnitTests.Security;

public sealed class JwtRsaStartupTests(RsaKeyFixture fixture) : IClassFixture<RsaKeyFixture>
{
    [Theory]
    [InlineData("Database:ConnectionString", "", "Database configuration")]
    [InlineData("Database:ConnectionString", "Server=unused;Database=unused;synthetic-secret=bad", "Database configuration")]
    [InlineData("Jwt:AccessTokenExpirationMinutes", "synthetic-secret", "invalid value")]
    [InlineData("Jwt:AccessTokenExpirationMinutes", "0", "lifetime")]
    [InlineData("Jwt:RefreshTokenExpirationDays", "0", "lifetime")]
    [InlineData("Jwt:RefreshTokenExpirationDays", "999999999", "lifetime")]
    [InlineData("Jwt:KeyId", "unsafe\nsynthetic-secret", "key ID")]
    [InlineData("Jwt:Issuer", "", "issuer")]
    [InlineData("Jwt:Audience", "", "audience")]
    [InlineData("Jwt:PrivateKeyPath", "", "private key path")]
    [InlineData("Jwt:PublicKeyPath", "", "public key path")]
    [InlineData("Jwt:KeyId", "", "key ID")]
    [InlineData("Jwt:PrivateKeyPath", "missing.pem", "private key file cannot be read")]
    [InlineData("Jwt:PublicKeyPath", "missing.pem", "public key file cannot be read")]
    [InlineData("Jwt:PrivateKeyPath", "invalid.pem", "private key file is not a valid")]
    [InlineData("Jwt:PublicKeyPath", "invalid.pem", "public key file is not a valid")]
    [InlineData("Jwt:PrivateKeyPath", "public.pem", "must contain an RSA private key")]
    public async Task InvalidConfigurationFailsDuringStartup(string key, string value, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(fixture.DirectoryPath, "invalid.pem"), "invalid PEM");
        await using var app = CreateApplication(key, value);
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync());
        Assert.Contains(message, exception.Message);
        Assert.DoesNotContain("synthetic-secret", exception.ToString());
    }

    [Fact]
    public async Task MismatchedKeyPairFailsDuringStartup()
    {
        using var otherRsa = RSA.Create(2048);
        await File.WriteAllTextAsync(Path.Combine(fixture.DirectoryPath, "other-public.pem"),
            otherRsa.ExportSubjectPublicKeyInfoPem());
        await using var app = CreateApplication("Jwt:PublicKeyPath", "other-public.pem");

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync());
        Assert.Contains("matching RSA key pair", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupLoadsKeysFromContentRootOrAbsolutePaths(bool absolutePaths)
    {
        await using var app = CreateApplication(absolutePaths: absolutePaths);
        await app.StartAsync();
        try
        {
            var service = app.Services.GetRequiredService<ITokenService>();
            var token = service.CreateTokens(Guid.NewGuid(), "learner", "learner@example.com", UserRole.User);
            var parameters = app.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
                .Get(JwtBearerDefaults.AuthenticationScheme).TokenValidationParameters;
            var publicKey = Assert.IsType<RsaSecurityKey>(parameters.IssuerSigningKey);
            Assert.Null(publicKey.Parameters.D);
            Assert.Equal(RsaKeyFixture.KeyId, new JwtSecurityTokenHandler().ReadJwtToken(token.AccessToken).Header.Kid);
            var principal = new JwtSecurityTokenHandler().ValidateToken(token.AccessToken, parameters, out _);
            Assert.True(principal.IsInRole("User"));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private WebApplication CreateApplication(string? key = null, string? value = null, bool absolutePaths = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = fixture.DirectoryPath,
            EnvironmentName = "Testing"
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var configuration = new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = "Server=unused;Database=unused;Integrated Security=True",
            ["Jwt:PrivateKeyPath"] = absolutePaths ? fixture.PrivateKeyPath : "private.pem",
            ["Jwt:PublicKeyPath"] = absolutePaths ? fixture.PublicKeyPath : "public.pem",
            ["Jwt:KeyId"] = RsaKeyFixture.KeyId,
            ["Jwt:Issuer"] = "JapaneseLearning.User",
            ["Jwt:Audience"] = "JapaneseLearning",
            ["Jwt:AccessTokenExpirationMinutes"] = "15",
            ["Jwt:RefreshTokenExpirationDays"] = "7"
        };
        if (key is not null)
            configuration[key] = value;
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddJwtAuthentication();
        builder.Services.AddAuthorization();
        builder.Services.AddHttpContextAccessor();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }
}
