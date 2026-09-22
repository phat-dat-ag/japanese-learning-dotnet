using JapaneseLearning.User.Infrastructure.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JapaneseLearning.User.UnitTests.Security;

public sealed class DatabaseConfigurationTests
{
    [Fact]
    public void SplitSettingsPreservePasswordAndPreventConnectionStringInjection()
    {
        const string password = "synthetic;Password='\";Server=attacker;$value";
        var options = new DatabaseOptions
        {
            Server = "localhost,1433", Name = "test", User = "test", Password = password,
            TrustServerCertificate = true
        };
        var parsed = new SqlConnectionStringBuilder(options.GetConnectionString());
        Assert.Equal(password, parsed.Password);
        Assert.Equal("localhost,1433", parsed.DataSource);
        Assert.False(parsed.PersistSecurityInfo);
        Assert.True(parsed.TrustServerCertificate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Password=synthetic-secret")]
    [InlineData("Server=local;Database=test")]
    [InlineData("Server=local;Database=test;User Id=test")]
    [InlineData("Server=local;Database=test;synthetic-secret=invalid")]
    public void MissingOrMalformedConnectionStringHasSafeError(string connectionString)
    {
        var options = new DatabaseOptions { ConnectionString = connectionString };
        var error = Assert.Throws<OptionsValidationException>(() => options.GetConnectionString());
        Assert.DoesNotContain("synthetic-secret", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void IntegratedAuthenticationRemainsSupportedWithoutPassword()
    {
        var options = new DatabaseOptions { ConnectionString = "Server=local;Database=test;Integrated Security=True" };
        Assert.True(new SqlConnectionStringBuilder(options.GetConnectionString()).IntegratedSecurity);
    }

    [Fact]
    public void AmbiguousSourcesFailInsteadOfSilentlyUsingStaleCredentials()
    {
        var options = new DatabaseOptions
        {
            ConnectionString = "Server=local;Database=test", Password = "synthetic-secret"
        };
        Assert.Throws<OptionsValidationException>(() => options.GetConnectionString());
    }
}
