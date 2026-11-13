using System.Diagnostics;
using System.Net;
using System.Text;
using JapaneseLearning.User.Api.Common.Logging;
using JapaneseLearning.User.Api.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Prometheus;

namespace JapaneseLearning.User.UnitTests.Observability;

public sealed class DependencyMetricsTests
{
    [Theory]
    [InlineData(200, "success")]
    [InlineData(503, "failure")]
    [InlineData(0, "failure")]
    [InlineData(-1, "timeout")]
    [InlineData(-2, "cancelled")]
    public async Task ActualJwksAttemptsHaveOneBoundedDurationObservation(int status, string outcome)
    {
        using var metrics = new ApplicationMetrics();
        using var cancellation = new CancellationTokenSource();
        using var handler = new JwksLoggingHandler(new HttpContextAccessor(),
            NullLogger<JwksLoggingHandler>.Instance, metrics) { InnerHandler = new FakeBackend(status, cancellation) };
        using var client = new HttpClient(handler);
        try
        {
            using var response = await client.GetAsync("http://synthetic.invalid/jwks?token=secret-sentinel", cancellation.Token);
            Assert.Equal(status, (int)response.StatusCode);
        }
        catch (HttpRequestException) when (status == 0) { }
        catch (OperationCanceledException) when (status is -1 or -2) { }

        var text = await Export(metrics.Registry);
        Assert.Contains($"app_dependency_duration_seconds_count{{dependency=\"jwks-api\",operation=\"jwks.fetch\",outcome=\"{outcome}\"}} 1", text);
        Assert.DoesNotContain("synthetic.invalid", text);
        Assert.DoesNotContain("secret-sentinel", text);
        Assert.DoesNotContain("trace_id", text);
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, $"p04-dependency-{outcome}.prom"), text);
    }

    [Fact]
    public async Task SqlProviderDiagnosticsNeverInspectPayloadAndOnlyExposeApprovedCounters()
    {
        var registry = Metrics.NewCustomRegistry();
        using var metrics = new SqlClientMetrics(Metrics.WithCustomRegistry(registry));
        using var source = new DiagnosticListener("SqlClientDiagnosticListener");
        foreach (var suffix in new[] { "WriteCommandAfter", "WriteCommandError", "WriteConnectionOpenAfter", "WriteConnectionOpenError" })
            source.Write("Microsoft.Data.SqlClient." + suffix, new ForbiddenPayload());
        source.Write("unapproved-secret-sentinel", new ForbiddenPayload());
        metrics.ObservePoolCounter("number-of-active-connections", 3);
        metrics.ObservePoolCounter("number-of-free-connections", 7);
        metrics.ObservePoolCounter("number-of-pooled-connections", 10);
        metrics.ObservePoolCounter("secret-sentinel", 100);
        metrics.ObservePoolCounter("number-of-active-connections", double.NaN);

        var text = await Export(registry);
        Assert.Contains("app_database_pool_connections{state=\"active\"} 3", text);
        Assert.Contains("app_database_pool_connections{state=\"idle\"} 7", text);
        Assert.Contains("app_database_pool_connections{state=\"pooled\"} 10", text);
        Assert.Contains("operation=\"command\",outcome=\"failure\"} 1", text);
        Assert.Contains("operation=\"connect\",outcome=\"success\"} 1", text);
        Assert.DoesNotContain("sentinel", text);
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "p04-sql-fixture.prom"), text);
    }

    [Fact]
    public async Task InvalidDurationAndUnknownOutcomeCannotExpandLabels()
    {
        using var metrics = new ApplicationMetrics();
        for (var i = 0; i < 10000; i++)
            metrics.JwksCompleted("secret-sentinel-" + i, 0.1);
        metrics.JwksCompleted("success", double.NaN);
        metrics.JwksCompleted("success", -1);
        var text = await Export(metrics.Registry);
        Assert.Contains("outcome=\"failure\"} 10000", text);
        Assert.DoesNotContain("sentinel", text);
        Assert.DoesNotContain("outcome=\"success\"", text);
    }

    private static async Task<string> Export(CollectorRegistry registry)
    {
        using var stream = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed class ForbiddenPayload
    {
        public string ConnectionString => throw new InvalidOperationException("Must never inspect payload");
        public override string ToString() => throw new InvalidOperationException("Must never render payload");
    }

    private sealed class FakeBackend(int status, CancellationTokenSource cancellation) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (status == -2)
                cancellation.Cancel();
            return status switch
            {
                0 => throw new HttpRequestException("secret-sentinel"),
                -1 => throw new OperationCanceledException("secret-sentinel"),
                -2 => throw new OperationCanceledException(cancellationToken),
                _ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status))
            };
        }
    }
}
