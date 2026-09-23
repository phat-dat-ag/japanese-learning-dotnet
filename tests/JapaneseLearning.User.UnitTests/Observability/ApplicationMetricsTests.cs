using System.Net;
using JapaneseLearning.User.Api.Common.Errors;
using JapaneseLearning.User.Api.Common.Logging;
using JapaneseLearning.User.Api.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JapaneseLearning.User.UnitTests.Observability;

public sealed class ApplicationMetricsTests
{
    [Fact]
    public async Task MetricsCountFinalStatusesAndLatencyWithoutSensitiveOrUnboundedLabels()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ApplicationMetrics>();
        builder.Services.AddApiErrorHandling();
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseRouting();
        app.UseApplicationMetrics();
        app.UseApiErrorHandling();
        app.MapGet("/api/auth/me", () => Results.Unauthorized());
        app.MapGet("/forbidden", () => Results.StatusCode(403));
        app.MapGet("/failure", (Func<string>)(() => throw new InvalidOperationException("credential-sentinel")));
        app.MapGet("/health/live", () => "Healthy");
        app.MapApplicationMetrics();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)) };
            client.DefaultRequestHeaders.Add("X-Correlation-ID", "metrics-correlation-sentinel");
            client.DefaultRequestHeaders.Add("Authorization", "Bearer credential-sentinel");
            foreach (var (path, status) in new[] { ("/api/auth/me", 401), ("/forbidden", 403), ("/failure", 500) })
            {
                using var response = await client.GetAsync(path + "?password=credential-sentinel");
                Assert.Equal(status, (int)response.StatusCode);
                Assert.Equal("metrics-correlation-sentinel", response.Headers.GetValues("X-Correlation-ID").Single());
            }
            for (var index = 0; index < 20; index++)
            {
                using var request = new HttpRequestMessage(new HttpMethod("SECRET" + index),
                    "/unmatched-credential-sentinel/" + index);
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
            await client.GetAsync("/health/live");
            using var scrape = new HttpRequestMessage(HttpMethod.Get, "/metrics");
            scrape.Headers.Add("Accept", "application/openmetrics-text; version=1.0.0");
            using var result = await client.SendAsync(scrape);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            var text = await result.Content.ReadAsStringAsync();
            Assert.Contains("http_requests_received_total{http_method=\"GET\",route=\"me\",code=\"401\"} 1", text);
            Assert.Contains("code=\"403\"", text);
            Assert.Contains("code=\"500\"", text);
            Assert.Contains("http_method=\"OTHER\",route=\"other\",code=\"404\"} 20", text);
            Assert.Contains("http_request_duration_seconds_bucket", text);
            Assert.Contains("process_working_set_bytes", text);
            Assert.Contains("dotnet_collection_count_total", text);
            foreach (var sensitive in new[] { "credential-sentinel", "metrics-correlation-sentinel", "SECRET",
                         "trace_id", "span_id", "user_id", "password", "http_method=\"GET\",route=\"other\",code=\"200\"" })
                Assert.DoesNotContain(sensitive, text);
        }
        finally { await app.StopAsync(); }
    }
}
