using JapaneseLearning.User.Infrastructure.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System.Data;

namespace JapaneseLearning.User.Infrastructure.Database;

public interface ISqlConnectionFactory
{
    IDbConnection CreateConnection();
}

public sealed class SqlConnectionFactory(
    IOptions<DatabaseOptions> options)
    : ISqlConnectionFactory
{
    private readonly string _connectionString =
        options.Value.GetConnectionString();

    public IDbConnection CreateConnection()
    {
        return new SqlConnection(_connectionString);
    }
}