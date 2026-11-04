using JapaneseLearning.User.Infrastructure.Configuration;
using JapaneseLearning.User.Infrastructure.Database;
using JapaneseLearning.User.Infrastructure.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using JapaneseLearning.User.Application.Abstractions.Persistence;
using JapaneseLearning.User.Application.Abstractions.Security;
using JapaneseLearning.User.Infrastructure.Persistence.Repositories;
using JapaneseLearning.User.Infrastructure.Security;
using System.Globalization;
using System.Text.RegularExpressions;

namespace JapaneseLearning.User.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<DatabaseOptions>()
            .Configure(options => BindSafely(configuration.GetSection(DatabaseOptions.SectionName), options))
            .Validate(options => { options.GetConnectionString(); return true; })
            .ValidateOnStart();

        services
            .AddOptions<JwtOptions>()
            .Configure(options =>
            {
                BindSafely(configuration.GetSection(JwtOptions.SectionName), options);
                BindSigningKeys(configuration, options);
            })
            .Validate(
                options => options.SigningKeys.Count > 0
                    && options.SigningKeys.Count(key => key.Active) == 1
                    && options.SigningKeys.All(key => IsValidKeyId(key.Kid)
                        && !string.IsNullOrWhiteSpace(key.PrivateKeyPath))
                    && options.SigningKeys.Select(key => key.Kid).Distinct(StringComparer.Ordinal).Count()
                        == options.SigningKeys.Count,
                "JWT signing keys require unique valid key IDs, nonblank private key paths and exactly one active key.")
            .Validate(
                options => Uri.TryCreate(options.JwksUrl, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttps
                        || (!options.RequireHttpsMetadata && uri.Scheme == Uri.UriSchemeHttp))
                    && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment),
                "JWT JWKS URL must be an absolute HTTPS URL (HTTP requires RequireHttpsMetadata=false), without credentials or fragment.")
            .Validate(
                options => IsSafeIdentifier(options.Issuer),
                "JWT issuer is missing or invalid.")
            .Validate(
                options => IsSafeIdentifier(options.Audience),
                "JWT audience is missing or invalid.")
            .Validate(
                options => options.AccessTokenExpirationMinutes is >= 1 and <= 1440,
                "JWT access-token lifetime must be between 1 and 1440 minutes.")
            .Validate(
                options => options.RefreshTokenExpirationDays is >= 1 and <= 365,
                "JWT refresh-token lifetime must be between 1 and 365 days.")
            .ValidateOnStart();

        services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();

        services.AddScoped<IUserRepository, UserRepository>();

        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();

        services.AddScoped<IPasswordHasher, PasswordHasher>();

        services.AddSingleton<JwtRsaKeys>();
        services.AddHostedService(provider => provider.GetRequiredService<JwtRsaKeys>());
        services.AddSingleton<ITokenService, TokenService>();

        services.AddScoped<ICurrentUser, CurrentUser>();

        services.AddHealthChecks()
            .AddCheck<SqlServerHealthCheck>(
                "sql-server",
                tags: ["ready"],
                timeout: TimeSpan.FromSeconds(3));

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IPasswordHasher, PasswordHasher>();

        return services;
    }

    private static bool IsSafeIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);

    private static bool IsValidKeyId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static void BindSigningKeys(IConfiguration configuration, JwtOptions options)
    {
        // ASP.NET's environment provider maps double underscores, not Spring-style
        // single-underscore names. Adapt the explicit signing-key contract only.
        var entries = new SortedDictionary<int, JwtSigningKeyOptions>();
        foreach (var entry in configuration.AsEnumerable())
        {
            var match = Regex.Match(entry.Key,
                "^APP_SECURITY_SIGNING_KEYS_([0-9]+)_(KID|PRIVATE_KEY_PATH|ACTIVE)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                if (entry.Key.StartsWith("APP_SECURITY_SIGNING_KEYS_", StringComparison.OrdinalIgnoreCase))
                    throw InvalidSigningKeys();
                continue;
            }
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                throw InvalidSigningKeys();
            if (!entries.TryGetValue(index, out var key))
                entries[index] = key = new JwtSigningKeyOptions();
            switch (match.Groups[2].Value.ToUpperInvariant())
            {
                case "KID": key.Kid = entry.Value ?? ""; break;
                case "PRIVATE_KEY_PATH": key.PrivateKeyPath = entry.Value ?? ""; break;
                case "ACTIVE":
                    if (!bool.TryParse(entry.Value, out var active))
                        throw InvalidSigningKeys();
                    key.Active = active;
                    break;
            }
        }
        if (entries.Count == 0)
            return;
        if (options.SigningKeys.Count > 0
            || !entries.Keys.SequenceEqual(Enumerable.Range(0, entries.Count)))
            throw InvalidSigningKeys();
        options.SigningKeys = entries.Values.ToList();
    }

    private static OptionsValidationException InvalidSigningKeys() =>
        new(Options.DefaultName, typeof(JwtOptions),
            ["JWT signing-key configuration is invalid. Use one list source, contiguous indices starting at zero and boolean active flags."]);

    private static void BindSafely<T>(IConfigurationSection section, T options) where T : class
    {
        try
        {
            section.Bind(options);
        }
        catch (InvalidOperationException)
        {
            // Binding/conversion exceptions can include the rejected value.
            throw new OptionsValidationException(Options.DefaultName, typeof(T),
                [$"{section.Key} configuration has an invalid value. Check the documented setting types."]);
        }
    }
}
