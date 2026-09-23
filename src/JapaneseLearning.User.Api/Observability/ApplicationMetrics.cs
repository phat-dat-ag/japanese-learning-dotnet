using Microsoft.AspNetCore.Routing;
using Prometheus;
using Prometheus.HttpMetrics;

namespace JapaneseLearning.User.Api.Observability;

public sealed class ApplicationMetrics
{
    public CollectorRegistry Registry { get; } = Metrics.NewCustomRegistry();
    private readonly Counter _requests;
    private readonly Histogram _duration;
    private readonly Gauge _inProgress;

    public ApplicationMetrics()
    {
        // An isolated registry avoids automatically exporting arbitrary Meters/EventCounters.
        DotNetStats.Register(Registry);
        var factory = Metrics.WithCustomRegistry(Registry);
        factory.ExemplarBehavior = ExemplarBehavior.NoExemplars();
        _requests = factory.CreateCounter("http_requests_received_total", "Completed HTTP requests.",
            ["http_method", "route", "code"]);
        _duration = factory.CreateHistogram("http_request_duration_seconds", "HTTP request duration.",
            new HistogramConfiguration
            {
                LabelNames = ["http_method", "route", "code"],
                Buckets = [0.01, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10]
            });
        _inProgress = factory.CreateGauge("http_requests_in_progress", "Active HTTP requests.",
            ["http_method", "route"]);
    }

    public void ConfigureHttp(HttpMiddlewareExporterOptions options)
    {
        options.RequestCount.Counter = _requests;
        options.RequestDuration.Histogram = _duration;
        options.InProgress.Gauge = _inProgress;
        options.AddCustomLabel("http_method", context => context.Request.Method switch
        {
            "GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE" or "OPTIONS" => context.Request.Method,
            _ => "OTHER"
        });
        options.AddCustomLabel("route", context =>
            (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText?.Trim('/').ToLowerInvariant() switch
            {
                "api/auth/login" => "login",
                "api/auth/register" => "register",
                "api/auth/refresh" => "refresh",
                "api/auth/logout" => "logout",
                "api/auth/me" => "me",
                "api/auth/admin-test" => "admin",
                ".well-known/jwks.json" => "jwks",
                _ => "other"
            });
        options.ConfigureMeasurements(measurement => measurement.ExemplarPredicate = _ => false);
    }
}

public static class ApplicationMetricsExtensions
{
    public static void UseApplicationMetrics(this WebApplication app)
    {
        var metrics = app.Services.GetRequiredService<ApplicationMetrics>();
        // Keep probe/scrape traffic out of application request rates.
        app.UseWhen(context => !context.Request.Path.StartsWithSegments("/health"),
            branch => branch.UseHttpMetrics(metrics.ConfigureHttp));
    }

    public static void MapApplicationMetrics(this WebApplication app)
    {
        var metrics = app.Services.GetRequiredService<ApplicationMetrics>();
        app.MapMetrics("/metrics", metrics.Registry).AllowAnonymous();
    }
}
