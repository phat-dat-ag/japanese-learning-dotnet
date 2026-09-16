using System.Security.Cryptography;
using JapaneseLearning.User.Infrastructure.Configuration;
using JapaneseLearning.User.Infrastructure.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;

namespace JapaneseLearning.User.UnitTests.Security;

// Ephemeral test credentials live only in the OS temp directory, never in the repository.
public sealed class RsaKeyFixture : IAsyncLifetime
{
    public const string KeyId = "jwt-contract-tests";
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "jwt-tests-" + Guid.NewGuid());
    public string PrivateKeyPath => Path.Combine(DirectoryPath, "private.pem");
    public string PublicKeyPath => Path.Combine(DirectoryPath, "public.pem");
    public JwtRsaKeys Keys { get; private set; } = null!;

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
    }

    public Task DisposeAsync()
    {
        Directory.Delete(DirectoryPath, recursive: true);
        return Task.CompletedTask;
    }
}
