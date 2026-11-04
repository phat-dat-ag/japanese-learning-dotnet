using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using JapaneseLearning.User.Application.Abstractions.Security;
using JapaneseLearning.User.Domain.Users;
using JapaneseLearning.User.Infrastructure;
using JapaneseLearning.User.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace JapaneseLearning.User.UnitTests.Security;

public sealed class SigningKeyListTests(RsaKeyFixture fixture) : IClassFixture<RsaKeyFixture>, IAsyncLifetime
{
    private readonly RSA _secondKey = RSA.Create(2048);
    private readonly string _secondFileName = "rotation-" + Guid.NewGuid() + ".pem";
    private string SecondPath => Path.Combine(fixture.DirectoryPath, _secondFileName);

    public async Task InitializeAsync() =>
        await File.WriteAllTextAsync(SecondPath, _secondKey.ExportPkcs8PrivateKeyPem());

    public Task DisposeAsync()
    {
        _secondKey.Dispose();
        File.Delete(SecondPath);
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task IssuesRs256TokenWithSelectedActiveKeyAndUnchangedClaims(int activeIndex)
    {
        var settings = Settings();
        settings["APP_SECURITY_SIGNING_KEYS_0_ACTIVE"] = (activeIndex == 0).ToString();
        settings["APP_SECURITY_SIGNING_KEYS_1_ACTIVE"] = (activeIndex == 1).ToString();
        using var provider = Provider(settings);
        await provider.GetRequiredService<JwtRsaKeys>().StartAsync(default);
        var userId = Guid.NewGuid();
        var result = provider.GetRequiredService<ITokenService>()
            .CreateTokens(userId, "learner", "learner@example.com", UserRole.Admin);
        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(result.AccessToken);
        Assert.Equal("user-rsa-v" + (activeIndex + 1), token.Header.Kid);
        Assert.Equal("RS256", token.Header.Alg);
        Assert.Equal(userId.ToString(), token.Subject);
        Assert.Equal("Admin", token.Payload["role"]);
        Assert.Equal("learner", token.Payload["unique_name"]);
        Assert.Equal("learner@example.com", token.Payload["email"]);
        Assert.False(token.Payload.ContainsKey("iat"));
        Assert.Equal(900, result.AccessTokenExpiresIn);
        var selected = activeIndex == 0 ? fixture.Keys.SigningKey.Parameters : _secondKey.ExportParameters(false);
        var publicKey = new RsaSecurityKey(new RSAParameters
        {
            Modulus = selected.Modulus,
            Exponent = selected.Exponent
        }) { KeyId = token.Header.Kid };
        var principal = handler.ValidateToken(result.AccessToken, new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = publicKey,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ValidateIssuer = true,
            ValidIssuer = "JapaneseLearning.User",
            ValidateAudience = true,
            ValidAudience = "JapaneseLearning",
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.Zero
        }, out _);
        Assert.True(principal.IsInRole("Admin"));
    }

    [Theory]
    [InlineData("missing-list", "exactly one active")]
    [InlineData("legacy-only", "exactly one active")]
    [InlineData("no-active", "exactly one active")]
    [InlineData("two-active", "exactly one active")]
    [InlineData("duplicate-kid", "unique valid key IDs")]
    [InlineData("blank-kid", "unique valid key IDs")]
    [InlineData("unsafe-kid", "unique valid key IDs")]
    [InlineData("missing-path", "nonblank private key paths")]
    [InlineData("invalid-boolean", "boolean active flags")]
    [InlineData("gap", "contiguous indices")]
    [InlineData("unknown-field", "configuration is invalid")]
    [InlineData("unreadable-active", "private key file cannot be read")]
    [InlineData("unreadable-inactive", "private key file cannot be read")]
    [InlineData("public-only", "must contain an RSA private key")]
    [InlineData("invalid-pem", "not a valid unencrypted RSA PEM")]
    [InlineData("weak-key", "at least 2048 bits")]
    public async Task InvalidSigningListFailsStartupWithoutExposingRejectedValues(string scenario, string expected)
    {
        var settings = Settings();
        switch (scenario)
        {
            case "missing-list":
            case "legacy-only":
                foreach (var key in settings.Keys.Where(k => k.StartsWith("APP_SECURITY_SIGNING_KEYS_")).ToArray())
                    settings.Remove(key);
                if (scenario == "legacy-only")
                {
                    settings["Jwt:PrivateKeyPath"] = fixture.PrivateKeyPath;
                    settings["Jwt:PublicKeyPath"] = fixture.PublicKeyPath;
                    settings["Jwt:KeyId"] = "retired-setting";
                }
                break;
            case "no-active": settings["APP_SECURITY_SIGNING_KEYS_1_ACTIVE"] = "false"; break;
            case "two-active": settings["APP_SECURITY_SIGNING_KEYS_0_ACTIVE"] = "true"; break;
            case "duplicate-kid": settings["APP_SECURITY_SIGNING_KEYS_1_KID"] = "user-rsa-v1"; break;
            case "blank-kid": settings["APP_SECURITY_SIGNING_KEYS_1_KID"] = ""; break;
            case "unsafe-kid": settings["APP_SECURITY_SIGNING_KEYS_1_KID"] = "unsafe\nsynthetic-secret"; break;
            case "missing-path": settings.Remove("APP_SECURITY_SIGNING_KEYS_1_PRIVATE_KEY_PATH"); break;
            case "invalid-boolean": settings["APP_SECURITY_SIGNING_KEYS_1_ACTIVE"] = "synthetic-secret"; break;
            case "gap":
                foreach (var entry in settings.Where(e => e.Key.StartsWith("APP_SECURITY_SIGNING_KEYS_1_")).ToArray())
                {
                    settings.Remove(entry.Key);
                    settings[entry.Key.Replace("KEYS_1_", "KEYS_2_")] = entry.Value;
                }
                break;
            case "unknown-field": settings["APP_SECURITY_SIGNING_KEYS_1_ACTVE"] = "synthetic-secret"; break;
            case "unreadable-active": settings["APP_SECURITY_SIGNING_KEYS_1_PRIVATE_KEY_PATH"] = "synthetic-secret.pem"; break;
            case "unreadable-inactive": settings["APP_SECURITY_SIGNING_KEYS_0_PRIVATE_KEY_PATH"] = "synthetic-secret.pem"; break;
            case "public-only": settings["APP_SECURITY_SIGNING_KEYS_1_PRIVATE_KEY_PATH"] = fixture.PublicKeyPath; break;
            case "invalid-pem":
                await File.WriteAllTextAsync(SecondPath, "synthetic-secret");
                break;
            case "weak-key":
                using (var weak = RSA.Create(1024))
                    await File.WriteAllTextAsync(SecondPath, weak.ExportPkcs8PrivateKeyPem());
                break;
        }
        using var provider = Provider(settings);
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            provider.GetRequiredService<JwtRsaKeys>().StartAsync(default));
        Assert.Contains(expected, exception.Message);
        Assert.DoesNotContain("synthetic-secret", exception.ToString());
    }

    private Dictionary<string, string?> Settings() => new()
    {
        ["Database:ConnectionString"] = "Server=unused;Database=unused;Integrated Security=True",
        ["Jwt:Issuer"] = "JapaneseLearning.User",
        ["Jwt:Audience"] = "JapaneseLearning",
        ["Jwt:JwksUrl"] = fixture.JwksUrl,
        ["Jwt:RequireHttpsMetadata"] = "false",
        ["Jwt:AccessTokenExpirationMinutes"] = "15",
        ["Jwt:RefreshTokenExpirationDays"] = "7",
        ["APP_SECURITY_SIGNING_KEYS_0_KID"] = "user-rsa-v1",
        ["APP_SECURITY_SIGNING_KEYS_0_PRIVATE_KEY_PATH"] = fixture.PrivateKeyPath,
        ["APP_SECURITY_SIGNING_KEYS_0_ACTIVE"] = "false",
        ["APP_SECURITY_SIGNING_KEYS_1_KID"] = "user-rsa-v2",
        ["APP_SECURITY_SIGNING_KEYS_1_PRIVATE_KEY_PATH"] = SecondPath,
        ["APP_SECURITY_SIGNING_KEYS_1_ACTIVE"] = "true"
    };

    private ServiceProvider Provider(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IHostEnvironment>(e => e.ContentRootPath == fixture.DirectoryPath));
        services.AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services.BuildServiceProvider();
    }
}
