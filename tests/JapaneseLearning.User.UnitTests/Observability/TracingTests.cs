using System.Diagnostics;
using JapaneseLearning.User.Api.Observability;
using JapaneseLearning.User.Api.Common.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace JapaneseLearning.User.UnitTests.Observability;

[Collection("Tracing process state")]
public sealed class TracingTests
{
    [Theory]
    [InlineData(ActivityKind.Server, 500, ActivityStatusCode.Error)]
    [InlineData(ActivityKind.Server, 401, ActivityStatusCode.Unset)]
    [InlineData(ActivityKind.Client, 503, ActivityStatusCode.Error)]
    [InlineData(ActivityKind.Client, 401, ActivityStatusCode.Error)]
    public void HttpStatusClassificationDoesNotTreatServerRejectionsAsUnhandledErrors(
        ActivityKind kind, int status, ActivityStatusCode expected)
    {
        using var source = new ActivitySource("P06.http-status.fixture");
        using var listener = new ActivityListener
        {
            ShouldListenTo = candidate => candidate == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = source.StartActivity("http", kind);
        activity!.SetTag("http.response.status_code", status);
        activity.SetTag("url.full", "https://privacy-sentinel/?password=privacy-sentinel");
        new TracePrivacyProcessor().OnEnd(activity);
        Assert.Equal(expected, activity.Status);
        Assert.Null(activity.GetTagItem("url.full"));
    }

    [Fact]
    public void CrossStackRatioVectorsAndFixedWindowBudgetAgree()
    {
        var sampler = new BoundarySampler(0.1, 1, () => 42);
        var low = ActivityTraceId.CreateFromString("00000000000000010000000000000001");
        var high = ActivityTraceId.CreateFromString("abcdef0123456789abcdef0123456789");
        var parameters = new SamplingParameters(default, high, "test", ActivityKind.Server);
        Assert.Equal(SamplingDecision.Drop, sampler.ShouldSample(parameters).Decision);
        parameters = new SamplingParameters(default, low, "test", ActivityKind.Server);
        Assert.Equal(SamplingDecision.RecordAndSample, sampler.ShouldSample(parameters).Decision);
        Assert.Equal(SamplingDecision.Drop, sampler.ShouldSample(parameters).Decision);
    }
    [Fact]
    public async Task RealHttpCreatesOneClientBetweenTwoServersAndKeepsCorrelationIndependent()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Tracing:SampleRatio"] = "1",
            ["Tracing:ExportEnabled"] = "false"
        });
        var exporter = new CaptureExporter(fail: true);
        builder.Services.AddServiceTracing(builder.Configuration);
        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing
            .AddProcessor(new SimpleActivityExportProcessor(exporter)));
        await using var app = builder.Build();
        app.UseRouting();
        app.UseMiddleware<CorrelationIdMiddleware>();
        string? propagated = null;
        app.MapGet("/api/auth/admin-test", (Microsoft.AspNetCore.Http.HttpContext context) =>
        {
            propagated = context.Request.Headers["traceparent"].ToString();
            return "ok";
        });
        app.MapGet("/api/auth/me", async () =>
        {
            using var dependency = new HttpClient();
            return await dependency.GetStringAsync(Assert.Single(app.Urls) + "/api/auth/admin-test");
        });
        await app.StartAsync();
        try
        {
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, Assert.Single(app.Urls) + "/api/auth/me");
            request.Headers.Add("traceparent", "00-abcdef0123456789abcdef0123456789-0123456789abcdef-01");
            request.Headers.Add("X-Correlation-ID", "independent-correlation");
            using (SuppressInstrumentationScope.Begin())
            {
                using var response = await client.SendAsync(request);
                Assert.True(response.IsSuccessStatusCode);
                Assert.Equal("independent-correlation", response.Headers.GetValues("X-Correlation-ID").Single());
            }
            await app.StopAsync();
            app.Services.GetRequiredService<TracerProvider>().ForceFlush();
            var spans = exporter.Spans.Where(span =>
                span.TraceId.ToString() == "abcdef0123456789abcdef0123456789").ToArray();
            Assert.Equal(3, spans.Length);
            var servers = spans.Where(span => span.Kind == ActivityKind.Server).ToArray();
            var outgoing = Assert.Single(spans, span => span.Kind == ActivityKind.Client);
            Assert.Equal(2, servers.Length);
            Assert.All(spans, span => Assert.Equal("abcdef0123456789abcdef0123456789", span.TraceId.ToString()));
            var first = Assert.Single(servers, span => span.ParentSpanId.ToString() == "0123456789abcdef");
            Assert.Equal(first.SpanId, outgoing.ParentSpanId);
            Assert.Single(servers, span => span.ParentSpanId == outgoing.SpanId);
            Assert.Contains(outgoing.SpanId.ToString(), propagated);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("00-00000000000000000000000000000000-0123456789abcdef-01")]
    [InlineData("00-abcdef0123456789abcdef0123456789-0000000000000000-01")]
    [InlineData("00-abcdef0123456789abcdef0123456789-0123456789abcdef-01,00-bad")]
    public void InvalidContextIsRejected(string parent)
    {
        var propagation = new TraceContextPropagation();
        propagation.ExtractTraceIdAndState(parent, GetSingle, out var accepted, out var state);
        Assert.Null(accepted);
        Assert.Null(state);
    }

    [Fact]
    public void ValidContextPreservesLineageAndInjectsChildWithoutBaggage()
    {
        const string parent = "00-abcdef0123456789abcdef0123456789-0123456789abcdef-01";
        var propagation = new TraceContextPropagation();
        propagation.ExtractTraceIdAndState(parent, GetSingle, out var accepted, out _);
        Assert.Equal(parent, accepted);
        using var child = new Activity("test").SetParentId(accepted!).Start();
        child.AddBaggage("email", "privacy-sentinel");
        child.TraceStateString = "vendor=privacy-sentinel";
        var headers = new Dictionary<string, string>();
        propagation.Inject(child, headers, (carrier, key, value) =>
            ((Dictionary<string, string>)carrier!)[key] = value);
        Assert.Single(headers);
        Assert.StartsWith("00-abcdef0123456789abcdef0123456789-", headers["traceparent"]);
        Assert.Equal(ActivitySpanId.CreateFromString("0123456789abcdef"), child.ParentSpanId);
        Assert.DoesNotContain("privacy-sentinel", string.Join(',', headers.Values));
    }

    [Fact]
    public void DuplicateContextIsRejected()
    {
        new TraceContextPropagation().ExtractTraceIdAndState(null,
            (object? carrier, string name, out string? value, out IEnumerable<string>? values) =>
            {
                value = null;
                values = new[] { "first", "second" };
            }, out var accepted, out _);
        Assert.Null(accepted);
    }

    [Fact]
    public void PublicSampledParentCannotForceRecordingAndLocalChildrenAgree()
    {
        var sampled = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(),
            ActivityTraceFlags.Recorded, isRemote: true);
        var parameters = new SamplingParameters(sampled, sampled.TraceId, "test", ActivityKind.Server);
        Assert.Equal(SamplingDecision.Drop, new BoundarySampler(0).ShouldSample(parameters).Decision);
        Assert.Equal(SamplingDecision.RecordAndSample, new BoundarySampler(1).ShouldSample(parameters).Decision);
        var local = new ActivityContext(sampled.TraceId, sampled.SpanId, ActivityTraceFlags.Recorded);
        parameters = new SamplingParameters(local, local.TraceId, "child", ActivityKind.Client);
        Assert.Equal(SamplingDecision.RecordAndSample, new BoundarySampler(0).ShouldSample(parameters).Decision);
        var unsampled = new ActivityContext(sampled.TraceId, sampled.SpanId, ActivityTraceFlags.None, isRemote: true);
        parameters = new SamplingParameters(unsampled, unsampled.TraceId, "test", ActivityKind.Server);
        Assert.Equal(SamplingDecision.Drop, new BoundarySampler(1).ShouldSample(parameters).Decision);
    }

    [Fact]
    public void DatabasePrivacyRunsBeforeExport()
    {
        using var source = new ActivitySource("P06.SqlClient.fixture");
        var exporter = new CaptureExporter();
        using var provider = Sdk.CreateTracerProviderBuilder().AddSource(source.Name)
            .AddProcessor(new TracePrivacyProcessor())
            .AddProcessor(new SimpleActivityExportProcessor(exporter)).Build();
        using (var activity = source.StartActivity("SELECT secret FROM users", ActivityKind.Client))
        {
            activity!.SetTag("db.statement", "privacy-sentinel");
            activity.SetTag("db.query.parameter.password", "privacy-sentinel");
            activity.SetTag("user.id", "privacy-sentinel");
            activity.SetStatus(ActivityStatusCode.Error, "privacy-sentinel");
        }
        var span = Assert.Single(exporter.Spans);
        Assert.Equal("database.command", span.DisplayName);
        Assert.Equal("database", span.GetTagItem("error.type"));
        Assert.Null(span.StatusDescription);
        Assert.DoesNotContain(span.TagObjects, tag => tag.Value?.ToString()?.Contains("privacy-sentinel") == true);
    }

    [Fact]
    public void ActualOtlpSerializationKeepsResourceAndFailsClosedOnEvents()
    {
        using var source = new ActivitySource("P06.otlp.fixture");
        var handler = new OtlpCaptureHandler();
        var exporter = new SafeTraceExporter(new OtlpExporterOptions
        {
            Protocol = OtlpExportProtocol.HttpProtobuf,
            Endpoint = new Uri("http://fixture.invalid/v1/traces"),
            HttpClientFactory = () => new HttpClient(handler, disposeHandler: false),
            TimeoutMilliseconds = 1000
        });
        using var provider = Sdk.CreateTracerProviderBuilder().AddSource(source.Name)
            .SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService("user-api")
                .AddAttributes(new Dictionary<string, object> { ["service.namespace"] = "japanese-learning" }))
            .AddProcessor(new TracePrivacyProcessor())
            .AddProcessor(new SimpleActivityExportProcessor(exporter)).Build();
        using (var span = source.StartActivity("safe"))
            span!.SetTag("Authorization", "privacy-sentinel");
        var payload = System.Text.Encoding.UTF8.GetString(Assert.Single(handler.Payloads));
        Assert.Contains("user-api", payload);
        Assert.Contains("japanese-learning", payload);
        Assert.DoesNotContain("privacy-sentinel", payload);
        using (var span = source.StartActivity("unsafe"))
            span!.AddEvent(new ActivityEvent("privacy-sentinel"));
        Assert.Single(handler.Payloads);
    }

    private static void GetSingle(object? carrier, string name, out string? value,
        out IEnumerable<string>? values)
    {
        value = carrier as string;
        values = null;
    }

    private sealed class CaptureExporter(bool fail = false) : BaseExporter<Activity>
    {
        public System.Collections.Concurrent.ConcurrentBag<Activity> Spans { get; } = new();
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
                Spans.Add(activity);
            return fail ? ExportResult.Failure : ExportResult.Success;
        }
    }

    private sealed class OtlpCaptureHandler : HttpMessageHandler
    {
        public List<byte[]> Payloads { get; } = new();
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Payloads.Add(request.Content!.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult());
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Array.Empty<byte>())
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(Send(request, cancellationToken));
    }
}

[CollectionDefinition("Tracing process state", DisableParallelization = true)]
public sealed class TracingProcessStateCollection;
