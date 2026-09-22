using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JapaneseLearning.User.Infrastructure.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    // Standalone development can still supply one complete connection string.
    public string ConnectionString { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool TrustServerCertificate { get; set; }

    public string GetConnectionString()
    {
        try
        {
            var hasParts = new[] { Server, Name, User, Password }.Any(value => value.Length > 0);
            SqlConnectionStringBuilder builder;
            if (!string.IsNullOrWhiteSpace(ConnectionString) && !hasParts)
                builder = new SqlConnectionStringBuilder(ConnectionString);
            else if (string.IsNullOrEmpty(ConnectionString)
                     && new[] { Server, Name, User, Password }.All(value => !string.IsNullOrWhiteSpace(value)))
                builder = new SqlConnectionStringBuilder
                {
                    DataSource = Server,
                    InitialCatalog = Name,
                    UserID = User,
                    Password = Password,
                    TrustServerCertificate = TrustServerCertificate
                };
            else
                throw InvalidConfiguration();

            if (string.IsNullOrWhiteSpace(builder.DataSource) || string.IsNullOrWhiteSpace(builder.InitialCatalog))
                throw InvalidConfiguration();
            if (!builder.IntegratedSecurity
                && builder.Authentication is SqlAuthenticationMethod.NotSpecified or SqlAuthenticationMethod.SqlPassword
                && (string.IsNullOrWhiteSpace(builder.UserID) || string.IsNullOrWhiteSpace(builder.Password)))
                throw InvalidConfiguration();
            builder.PersistSecurityInfo = false;
            return builder.ConnectionString;
        }
        catch (ArgumentException)
        {
            // SqlClient parser messages may contain connection-string values.
            throw InvalidConfiguration();
        }
    }

    private static OptionsValidationException InvalidConfiguration() => new(
        Options.DefaultName, typeof(DatabaseOptions),
        ["Database configuration is invalid. Supply either a valid Database:ConnectionString with Server and Database, or Database:Server, Name, User and Password."]);
}
