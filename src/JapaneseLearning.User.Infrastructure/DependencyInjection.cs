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
            .Configure(options => BindSafely(configuration.GetSection(JwtOptions.SectionName), options))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.PrivateKeyPath),
                "JWT private key path is missing or empty.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.PublicKeyPath),
                "JWT public key path is missing or empty.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.KeyId) && options.KeyId.Length <= 128
                    && options.KeyId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'),
                "JWT key ID is missing or invalid; use 1-128 ASCII letters, digits, hyphens or underscores.")
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