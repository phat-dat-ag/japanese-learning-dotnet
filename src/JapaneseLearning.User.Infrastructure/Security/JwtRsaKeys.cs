using System.Security.Cryptography;
using JapaneseLearning.User.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace JapaneseLearning.User.Infrastructure.Security;

// Loaded before the HTTP server starts; no key-file I/O occurs during requests.
public sealed class JwtRsaKeys(
    IOptions<JwtOptions> options,
    IHostEnvironment environment) : IHostedService
{
    private RsaSecurityKey? _signingKey;

    public RsaSecurityKey SigningKey => _signingKey
        ?? throw new InvalidOperationException("JWT RSA keys have not been loaded.");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var configuration = options.Value;
        if (configuration.SigningKeys.Count > 0)
        {
            if (configuration.SigningKeys.Count(key => key.Active) != 1)
                throw ConfigurationError("Exactly one JWT signing key must be active.");
            RsaSecurityKey? activeKey = null;
            // Validate every configured private key, including inactive rotation keys.
            // Retain only the active signing material after startup.
            foreach (var key in configuration.SigningKeys)
            {
                using var rsa = await LoadKeyAsync(key.PrivateKeyPath, "private", cancellationToken);
                var parameters = ExportSigningParameters(rsa);
                if (key.Active)
                    activeKey = new RsaSecurityKey(parameters) { KeyId = key.Kid };
            }
            _signingKey = activeKey
                ?? throw ConfigurationError("Exactly one JWT signing key must be active.");
            return;
        }
        using var privateKey = await LoadKeyAsync(
            configuration.PrivateKeyPath, "private", cancellationToken);
        using var publicKey = await LoadKeyAsync(
            configuration.PublicKeyPath, "public", cancellationToken);

        var privateParameters = ExportSigningParameters(privateKey);

        if (privateKey.KeySize < 2048 || publicKey.KeySize < 2048)
            throw ConfigurationError("JWT RSA keys must be at least 2048 bits.");

        if (!privateKey.ExportSubjectPublicKeyInfo().AsSpan()
            .SequenceEqual(publicKey.ExportSubjectPublicKeyInfo()))
            throw ConfigurationError("JWT private and public key files do not contain a matching RSA key pair.");

        _signingKey = new RsaSecurityKey(privateParameters) { KeyId = configuration.KeyId };
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static RSAParameters ExportSigningParameters(RSA rsa)
    {
        try
        {
            var parameters = rsa.ExportParameters(true);
            if (rsa.KeySize < 2048)
                throw ConfigurationError("JWT RSA keys must be at least 2048 bits.");
            return parameters;
        }
        catch (CryptographicException)
        {
            throw ConfigurationError("JWT private key file must contain an RSA private key.");
        }
    }

    private async Task<RSA> LoadKeyAsync(string configuredPath, string kind, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw ConfigurationError($"JWT {kind} key path is missing or empty.");

        string pem;
        try
        {
            var path = Path.GetFullPath(configuredPath, environment.ContentRootPath);
            pem = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            throw ConfigurationError($"JWT {kind} key file cannot be read. Check Jwt:{(kind == "private" ? "PrivateKeyPath" : "PublicKeyPath")} and file permissions.");
        }

        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
            return rsa;
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            rsa.Dispose();
            throw ConfigurationError($"JWT {kind} key file is not a valid unencrypted RSA PEM file.");
        }
    }

    private static OptionsValidationException ConfigurationError(string message) =>
        new(Options.DefaultName, typeof(JwtOptions), [message]);
}
