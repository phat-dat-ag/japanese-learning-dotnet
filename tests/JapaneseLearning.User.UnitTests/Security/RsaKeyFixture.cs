using System.Security.Cryptography;
using JapaneseLearning.User.Infrastructure.Configuration;
using JapaneseLearning.User.Infrastructure.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace JapaneseLearning.User.UnitTests.Security;

// Ephemeral test credentials live only in the OS temp directory, never in the repository.
public sealed class RsaKeyFixture : IAsyncLifetime
{
    public const string KeyId = "jwt-contract-tests";
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "jwt-tests-" + Guid.NewGuid());
    public string PrivateKeyPath => Path.Combine(DirectoryPath, "private.pem");
    public string PublicKeyPath => Path.Combine(DirectoryPath, "public.pem");
    public JwtRsaKeys Keys { get; private set; } = null!;
    private WebApplication? _server;
    public string JwksUrl { get; private set; } = null!;
    public string PublishedJwks { get; set; } = "";
    public bool Unavailable { get; set; }
    public int Requests;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(DirectoryPath);
        using var rsa = RSA.Create(2048);
        await File.WriteAllTextAsync(PrivateKeyPath, rsa.ExportPkcs8PrivateKeyPem());
        await File.WriteAllTextAsync(PublicKeyPath, rsa.ExportSubjectPublicKeyInfoPem());
        Keys = new JwtRsaKeys(Options.Create(new JwtOptions
        {
            PrivateKeyPath = PrivateKeyPath,
            PublicKeyPath = PublicKeyPath,
            KeyId = KeyId
        }), Mock.Of<IHostEnvironment>(environment => environment.ContentRootPath == DirectoryPath));
        await Keys.StartAsync(CancellationToken.None);
        var publicJwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(
            new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = KeyId });
        publicJwk.Use = "sig";
        publicJwk.Alg = "RS256";
        PublishedJwks = JsonSerializer.Serialize(new { keys = new[] { publicJwk } });
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _server = builder.Build();
        _server.MapGet("/.well-known/jwks.json", () =>
        {
            Interlocked.Increment(ref Requests);
            return Unavailable ? Results.StatusCode(503) : Results.Text(PublishedJwks, "application/json");
        });
        await _server.StartAsync();
        JwksUrl = _server.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single() + "/.well-known/jwks.json";
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync();
        Directory.Delete(DirectoryPath, recursive: true);
    }
}
