using Microsoft.AspNetCore.Routing;
using Prometheus;
using Prometheus.HttpMetrics;

namespace JapaneseLearning.User.Api.Observability;

public sealed class ApplicationMetrics : IDisposable
{
    public CollectorRegistry Registry { get; } = Metrics.NewCustomRegistry();
    private readonly Counter _requests;
    private readonly Histogram _duration;
    private readonly Gauge _inProgress;
    private readonly Counter _security;
    private readonly Histogram _dependencies;
    private readonly SqlClientMetrics _sql;

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
                Buckets = [0.01, 0.05, 0.1, 0.25, 0.5, 1, 2, 2.5, 5, 10]
            });
        _inProgress = factory.CreateGauge("http_requests_in_progress", "Active HTTP requests.",
            ["http_method", "route"]);
        _security = factory.CreateCounter("app_security_events_total", "Terminal HTTP security outcomes; not unique users or tokens.",
            ["operation", "outcome"]);
        _dependencies = factory.CreateHistogram("app_dependency_duration_seconds", "Actual outbound JWKS HTTP attempts, through response headers.",
            new HistogramConfiguration
            {
                LabelNames = ["dependency", "operation", "outcome"],
                Buckets = [0.01, 0.05, 0.1, 0.25, 0.5, 1, 2, 2.5, 5, 10]
            });
        _sql = new SqlClientMetrics(factory);
    }

    public void JwksCompleted(string outcome, double seconds)
    {
        var bounded = outcome is "success" or "failure" or "timeout" or "cancelled" ? outcome : "failure";
        if (double.IsFinite(seconds) && seconds >= 0)
            _dependencies.WithLabels("jwks-api", "jwks.fetch", bounded).Observe(seconds);
    }

    public void SecurityCompleted(HttpContext context)
    {
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText?.Trim('/').ToLowerInvariant();
        var status = context.Response.StatusCode;
        var operation = context.Request.Method == "POST" ? route switch
        {
            "api/auth/login" => "login",
            "api/auth/register" => "register",
            "api/auth/refresh" => "refresh",
            "api/auth/logout" => "logout",
            _ => null
        } : null;
        operation ??= status == 403 ? "authorization" : status == 401 || context.User.Identity?.IsAuthenticated == true
            ? "authentication" : null;
        if (operation is null)
            return;
        var outcome = status switch
        {
            >= 200 and < 300 => "success",
            401 => "unauthenticated",
            403 => "denied",
            400 => "invalid_request",
            _ => "failure"
        };
        _security.WithLabels(operation, outcome).Inc();
    }

    public void Dispose() => _sql.Dispose();

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
        app.UseWhen(context => !context.Request.Path.StartsWithSegments("/health")
                && !context.Request.Path.StartsWithSegments("/metrics"), branch =>
        {
            branch.UseHttpMetrics(metrics.ConfigureHttp);
            branch.Use(async (context, next) =>
            {
                await next(context);
                metrics.SecurityCompleted(context);
            });
        });
    }

    public static void MapApplicationMetrics(this WebApplication app)
    {
        var metrics = app.Services.GetRequiredService<ApplicationMetrics>();
        app.MapMetrics("/metrics", metrics.Registry).AllowAnonymous();
    }
}
