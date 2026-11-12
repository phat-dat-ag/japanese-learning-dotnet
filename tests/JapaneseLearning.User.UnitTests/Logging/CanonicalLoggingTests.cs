using JapaneseLearning.User.Api.Common.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.Diagnostics;
using System.Text.Json;

namespace JapaneseLearning.User.UnitTests.Logging;

public sealed class CanonicalLoggingTests
{
    [Theory]
    [InlineData("valid_123", true)]
    [InlineData("", false)]
    [InlineData("bad value", false)]
    [InlineData("oversized", false)]
    public async Task CompletionIsTypedPrivateAndScoped(string incoming, bool preserved)
    {
        var sink = new Capture();

        using var log = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
        using var factory = LoggerFactory.Create(builder => builder.AddSerilog(log));

        var context = new DefaultHttpContext();

        context.Request.Headers["X-Correlation-ID"] = incoming == "oversized" ? new string('a', 65) : incoming;
        context.Request.Headers["Authorization"] = "Bearer token-sentinel";
        context.Request.Headers["Cookie"] = "cookie-sentinel";
        context.Request.QueryString = new QueryString("?password=query-sentinel");
        context.Request.Path = "/private-sentinel";
        context.Request.Method = "GET";

        var middleware = new CorrelationIdMiddleware(async http =>
        {
            await Task.Yield();
            http.Response.StatusCode = 401;
        }, factory.CreateLogger<CorrelationIdMiddleware>());

        await middleware.InvokeAsync(context);

        var entry = Assert.Single(sink.Events);

        using var writer = new StringWriter();

        new SafeJsonFormatter().Format(entry, writer);

        var text = writer.ToString();

        using var document = JsonDocument.Parse(text);

        var root = document.RootElement;

        Assert.Equal("user-api", root.GetProperty("service_name").GetString());
        Assert.Equal("http.request.completed", root.GetProperty("event_name").GetString());
        Assert.Equal("/unmatched", root.GetProperty("http_route").GetString());
        Assert.Equal("authentication", root.GetProperty("error_category").GetString());
        Assert.Equal("client_error", root.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("duration_ms").ValueKind);
        Assert.Equal(401, root.GetProperty("http_status").GetInt32());
        Assert.Equal(JsonValueKind.False, root.GetProperty("Properties").GetProperty("IsAuthenticated").ValueKind);

        var id = root.GetProperty("correlation_id").GetString();

        Assert.True(CanonicalLogFields.ValidCorrelation(id));

        if (preserved)
            Assert.Equal(incoming, id);

        foreach (var sentinel in new[] { "token-sentinel", "cookie-sentinel", "query-sentinel", "private-sentinel" })
            Assert.DoesNotContain(sentinel, text);

        factory.CreateLogger("after").LogInformation("After request");
        Assert.False(sink.Events.Last().Properties.ContainsKey("CorrelationId"));
    }

    [Theory]
    [InlineData("api/auth/me")]
    [InlineData("/api/auth/me")]
    public void AttributeRouteTemplatesHaveOneCanonicalLeadingSlash(string template)
    {
        var record = System.Text.Json.Nodes.JsonNode.Parse(
            "{\"Timestamp\":\"2026-11-12T00:00:00Z\",\"Level\":\"Information\",\"Properties\":{}}")!.AsObject();

        record["Properties"]!["RequestRoute"] = template;
        record["Properties"]!["RequestMethod"] = "GET";

        CanonicalLogFields.Add(record);

        Assert.Equal("/api/auth/me", record["http_route"]!.ToString());
        Assert.Equal("auth", record["route_family"]!.ToString());
    }

    [Fact]
    public void SyntheticFallbackWithoutActivityIsNotATrace()
    {
        var saved = Activity.Current;

        Activity.Current = null;

        try
        {
            using var writer = new StringWriter();

            var entry = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null,
                new Serilog.Parsing.MessageTemplateParser().Parse("No active span"),
                new[] { new LogEventProperty("CorrelationId", new ScalarValue("legacy-id")) });

            new SafeJsonFormatter().Format(entry, writer);

            using var document = JsonDocument.Parse(writer.ToString());

            Assert.False(document.RootElement.TryGetProperty("trace_id", out _));
            Assert.False(document.RootElement.TryGetProperty("span_id", out _));
        }
        finally { Activity.Current = saved; }
    }

    [Theory]
    [InlineData("Password")]
    [InlineData("RefreshToken")]
    [InlineData("Authorization")]
    [InlineData("Cookie")]
    [InlineData("PrivateKey")]
    [InlineData("ConnectionString")]
    [InlineData("UserId")]
    public void SensitiveStructuredArgumentsAreNotRendered(string property)
    {
        var entry = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error,
            new InvalidOperationException("exception-sentinel"),
            new Serilog.Parsing.MessageTemplateParser().Parse("Safe event {" + property + "}"),
            new[] { new LogEventProperty(property, new ScalarValue(property == "PrivateKey"
                ? "-----BEGIN PRIVATE KEY-----secret-sentinel" : "secret-sentinel")) });

        using var writer = new StringWriter();

        new SafeJsonFormatter().Format(entry, writer);

        Assert.DoesNotContain("secret-sentinel", writer.ToString());
        Assert.DoesNotContain("exception-sentinel", writer.ToString());

        using var document = JsonDocument.Parse(writer.ToString());

        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    private sealed class Capture : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
