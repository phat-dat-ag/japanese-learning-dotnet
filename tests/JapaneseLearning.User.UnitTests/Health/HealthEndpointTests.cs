using System.Data.Common;
using System.Net;
using JapaneseLearning.User.Api.Health;
using JapaneseLearning.User.Infrastructure;
using JapaneseLearning.User.Infrastructure.Database;
using JapaneseLearning.User.UnitTests.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace JapaneseLearning.User.UnitTests.Health;

public sealed class HealthEndpointTests(RsaKeyFixture fixture) : IClassFixture<RsaKeyFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadinessReflectsDatabaseFailureAndRecoveryWhileLivenessStaysHealthy(bool timeout)
    {
        var available = true;
        var connection = new Mock<DbConnection>();
        connection.Setup(value => value.OpenAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken token) => available ? Task.CompletedTask : timeout
                ? Task.Delay(Timeout.Infinite, token)
                : Task.FromException(new InvalidOperationException("sensitive-database-diagnostic")));
        var factory = new Mock<ISqlConnectionFactory>();
        factory.Setup(value => value.CreateConnection()).Returns(connection.Object);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = fixture.DirectoryPath,
            EnvironmentName = "Testing"
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = "Server=unused;Database=unused;Integrated Security=True",
            ["Jwt:PrivateKeyPath"] = fixture.PrivateKeyPath,
            ["Jwt:PublicKeyPath"] = fixture.PublicKeyPath,
            ["Jwt:KeyId"] = RsaKeyFixture.KeyId,
            ["Jwt:Issuer"] = "JapaneseLearning.User",
            ["Jwt:Audience"] = "JapaneseLearning",
            ["Jwt:AccessTokenExpirationMinutes"] = "15",
            ["Jwt:RefreshTokenExpirationDays"] = "7"
        });
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddSingleton(factory.Object);
        builder.Services.AddAuthorization(options => options.FallbackPolicy =
            new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        await using var app = builder.Build();
        app.UseAuthorization();
        app.MapServiceHealthChecks();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient
            {
                BaseAddress = new Uri(Assert.Single(app.Urls)),
                Timeout = TimeSpan.FromSeconds(8)
            };
            foreach (var databaseAvailable in new[] { true, false, true })
            {
                available = databaseAvailable;
                factory.Invocations.Clear();
                using var live = await client.GetAsync("/health/live");
                Assert.Equal(HttpStatusCode.OK, live.StatusCode);
                Assert.Equal("Healthy", await live.Content.ReadAsStringAsync());
                factory.Verify(value => value.CreateConnection(), Times.Never);

                foreach (var path in new[] { "/health/ready", "/health" })
                {
                    using var ready = await client.GetAsync(path);
                    Assert.Equal(available ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, ready.StatusCode);
                    Assert.Equal(available ? "Healthy" : "Unhealthy", await ready.Content.ReadAsStringAsync());
                    Assert.Equal("text/plain", ready.Content.Headers.ContentType?.MediaType);
                }
                factory.Verify(value => value.CreateConnection(), Times.Exactly(2));
            }
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
