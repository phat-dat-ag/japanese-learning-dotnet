using JapaneseLearning.User.Infrastructure.Configuration;
using JapaneseLearning.User.Infrastructure.Database;
using JapaneseLearning.User.Infrastructure.HealthChecks;
using Microsoft.Extensions.Configuration;
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
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .Validate(
                options => !string.IsNullOrWhiteSpace(
                    options.ConnectionString),
                "Database connection string is missing or empty.")
            .ValidateOnStart();

        services
            .AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.PrivateKeyPath),
                "JWT private key path is missing or empty.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.PublicKeyPath),
                "JWT public key path is missing or empty.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.KeyId),
                "JWT key ID is missing or empty.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Issuer),
                "JWT issuer is missing or empty.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Audience),
                "JWT audience is missing or empty.")
            .Validate(
                options => options.AccessTokenExpirationMinutes > 0,
                "JWT access-token lifetime must be positive.")
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
                "sql-server");

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IPasswordHasher, PasswordHasher>();

        return services;
    }
}