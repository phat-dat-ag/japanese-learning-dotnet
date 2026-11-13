using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Globalization;

namespace JapaneseLearning.User.Api.Observability;

public static class Tracing
{
    public static IServiceCollection AddServiceTracing(this IServiceCollection services, IConfiguration configuration)
    {
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;
        DistributedContextPropagator.Current = new TraceContextPropagation();
        Sdk.SetDefaultTextMapPropagator(new OpenTelemetry.Context.Propagation.TraceContextPropagator());

        var ratio = configuration.GetValue("Tracing:SampleRatio", 0.1);

        if (!double.IsFinite(ratio) || ratio < 0 || ratio > 1)
            throw new InvalidOperationException("Tracing:SampleRatio must be between zero and one.");

        var rootLimit = configuration.GetValue("Tracing:MaxRootsPerSecond", 10);

        if (rootLimit is < 1 or > 1000)
            throw new InvalidOperationException("Tracing:MaxRootsPerSecond must be between 1 and 1000.");

        var resource = ResourceBuilder.CreateEmpty().AddService("user-api", serviceVersion:
                    configuration["Observability:ServiceVersion"] ?? "unknown")
                .AddAttributes(new Dictionary<string, object>
                {
                    ["service.namespace"] = "japanese-learning",
                    ["deployment.environment.name"] = configuration["Tracing:Environment"] ?? "development"
                });
        services.AddOpenTelemetry().WithTracing(builder =>
            {
                builder.SetResourceBuilder(resource).SetSampler(new BoundarySampler(ratio, rootLimit))
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.RecordException = false;
                        options.Filter = context => !context.Request.Path.StartsWithSegments("/health")
                            && !context.Request.Path.StartsWithSegments("/metrics")
                            && !context.Request.Path.StartsWithSegments("/swagger");
                    })
                    .AddHttpClientInstrumentation(options => options.RecordException = false)
                    .AddSqlClientInstrumentation(options => options.RecordException = false)
                    .AddProcessor(new TracePrivacyProcessor());

                if (configuration.GetValue("Tracing:ExportEnabled", false))
                {
                    var endpoint = new Uri(configuration["Tracing:Endpoint"]
                        ?? "http://otel-collector:4317");

                    builder.AddProcessor(new BatchActivityExportProcessor(
                        new SafeTraceExporter(new OtlpExporterOptions
                        {
                            Endpoint = endpoint,
                            Protocol = OtlpExportProtocol.Grpc,
                            TimeoutMilliseconds = 3000
                        }), maxQueueSize: 1024, scheduledDelayMilliseconds: 1000,
                        exporterTimeoutMilliseconds: 3000, maxExportBatchSize: 128));
                }
            });
        return services;
    }
}

// A public remote sampled bit cannot force recording. Local children preserve the
// decision, and remote unsampled parents remain unsampled without breaking lineage.
public sealed class BoundarySampler(double ratio, int maxRootsPerSecond = 10,
    Func<long>? clock = null) : Sampler
{
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64 / 1000);
    private readonly object _gate = new();
    private long _window;
    private int _used;

    public override SamplingResult ShouldSample(in SamplingParameters parameters)
    {
        var parent = parameters.ParentContext;

        if (parent.TraceId != default && !parent.TraceFlags.HasFlag(ActivityTraceFlags.Recorded))
            return new SamplingResult(SamplingDecision.Drop);
        if (parent.TraceId != default && !parent.IsRemote)
            return new SamplingResult(SamplingDecision.RecordAndSample);

        // Same first-63-bit threshold in both SDKs; built-in ratio implementations
        // differ. A per-instance budget also bounds attacker-selected trace IDs.
        var value = ulong.Parse(parameters.TraceId.ToString()[..16], NumberStyles.HexNumber,
            CultureInfo.InvariantCulture) >> 1;

        if (ratio <= 0 || ratio < 1 && value >= ratio * long.MaxValue)
            return new SamplingResult(SamplingDecision.Drop);

        lock (_gate)
        {
            var window = _clock();

            if (window != _window)
            {
                _window = window;
                _used = 0;
            }

            return new SamplingResult(_used++ < maxRootsPerSecond
                ? SamplingDecision.RecordAndSample : SamplingDecision.Drop);
        }
    }
}

public sealed class TracePrivacyProcessor : BaseProcessor<Activity>
{
    private static readonly HashSet<string> Routes = new(StringComparer.OrdinalIgnoreCase)
    {
        "api/auth/login", "api/auth/register", "api/auth/refresh", "api/auth/logout",
        "api/auth/me", "api/auth/admin-test"
    };

    public override void OnEnd(Activity activity)
    {
        var method = activity.GetTagItem("http.request.method")?.ToString()
            ?? activity.GetTagItem("http.method")?.ToString();
        method = method is "GET" or "POST" or "PUT" or "DELETE" or "PATCH" or "HEAD" or "OPTIONS"
            ? method : "OTHER";
        var rawRoute = activity.GetTagItem("http.route")?.ToString()?.Trim('/');
        var route = rawRoute is not null && Routes.Contains(rawRoute) ? "/" + rawRoute : "/unmatched";
        var status = activity.GetTagItem("http.response.status_code") ?? activity.GetTagItem("http.status_code");
        var database = activity.Source.Name.Contains("SqlClient", StringComparison.Ordinal);

        foreach (var tag in activity.TagObjects.ToArray())
            activity.SetTag(tag.Key, null);
        activity.TraceStateString = null;
        activity.SetStatus(activity.Status, null);
        activity.DisplayName = database ? "database.command" : activity.Kind == ActivityKind.Server
            ? $"{method} {route}" : "HTTP dependency";

        if (database)
            activity.SetTag("db.system.name", "microsoft.sql_server");
        else
        {
            activity.SetTag("http.request.method", method);

            if (activity.Kind == ActivityKind.Server)
                activity.SetTag("http.route", route);

            if (int.TryParse(status?.ToString(), out var code) && code is >= 100 and <= 599)
            {
                activity.SetTag("http.response.status_code", code);

                if (code >= 500 || activity.Kind == ActivityKind.Client && code >= 400)
                    activity.SetStatus(ActivityStatusCode.Error);
            }
        }

        if (activity.Status == ActivityStatusCode.Error)
            activity.SetTag("error.type", database ? "database" : "transport_or_http");
    }
}

// Activity events/links are immutable. Fail closed if a future instrumentation
// records them, rather than allowing arbitrary exception messages to leave process.
public sealed class SafeTraceExporter(OtlpExporterOptions options) : OtlpTraceExporter(options)
{
    public override ExportResult Export(in Batch<Activity> batch)
    {
        var safe = new List<Activity>();
        foreach (var activity in batch)
        {
            if (activity.Events.Any() || activity.Links.Any())
                continue;

            safe.Add(activity);
        }

        if (safe.Count == 0)
            return ExportResult.Success;

        var items = safe.ToArray();

        return base.Export(new Batch<Activity>(items, items.Length));
    }

}
