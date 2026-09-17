using JapaneseLearning.User.Infrastructure.Database;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace JapaneseLearning.User.Infrastructure.HealthChecks;

public sealed class SqlServerHealthCheck(
    ISqlConnectionFactory connectionFactory)
    : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = connectionFactory.CreateConnection();

            if (connection is System.Data.Common.DbConnection dbConnection)
            {
                await dbConnection.OpenAsync(cancellationToken);

                return HealthCheckResult.Healthy(
                    "SQL Server is reachable.");
            }

            connection.Open();

            return HealthCheckResult.Healthy(
                "SQL Server is reachable.");
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy(
                "SQL Server is unavailable.");
        }
    }
}