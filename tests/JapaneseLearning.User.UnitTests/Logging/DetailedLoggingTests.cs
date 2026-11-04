using System.Diagnostics;
using System.Net;
using System.Text.Json;
using JapaneseLearning.User.Api.Common.Logging;
using JapaneseLearning.User.Application.Common.Behaviors;
using JapaneseLearning.User.Application.Common.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace JapaneseLearning.User.UnitTests.Logging;

public sealed class DetailedLoggingTests
{
    [Theory]
    [InlineData("vocabulary-api", "vocabulary-api")]
    [InlineData("credential-sentinel.invalid", "unknown")]
    [InlineData("", "unknown")]
    public async Task RequestContextCarriesPeerRouteAndParentTraceWithoutTrustingHeaders(string caller, string expected)
    {
        var sink = new CaptureSink();
        using var logger = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
        using var factory = LoggerFactory.Create(builder => builder.AddSerilog(logger));
        using var activity = new Activity("incoming").SetIdFormat(ActivityIdFormat.W3C)
            .SetParentId("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01").Start();
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/users/credential-sentinel";
        context.Request.QueryString = new QueryString("?token=credential-sentinel");
        context.Request.Headers["X-Caller-Service"] = caller;
        context.Request.Headers["X-Forwarded-For"] = "credential-sentinel";
        context.Request.Headers["Authorization"] = "Bearer credential-sentinel";
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.8");
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask,
            RoutePatternFactory.Parse("/users/{id}"), 0, EndpointMetadataCollection.Empty, "Users"));
        var middleware = new CorrelationIdMiddleware(http =>
        {
            factory.CreateLogger("Application").LogInformation("Inside application");
            http.Response.StatusCode = 403;
            return Task.CompletedTask;
        }, factory.CreateLogger<CorrelationIdMiddleware>());

        await middleware.InvokeAsync(context);

        Assert.Equal(2, sink.Events.Count);
        foreach (var entry in sink.Events)
        {
            Assert.Equal(expected, Scalar(entry, "ClaimedCallerService"));
            Assert.Equal("10.0.0.8", Scalar(entry, "PeerAddress"));
            Assert.Equal("/users/{id}", Scalar(entry, "RequestRoute"));
            Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", Scalar(entry, "TraceId"));
            Assert.Equal("00f067aa0ba902b7", Scalar(entry, "ParentSpanId"));
            Assert.DoesNotContain("credential-sentinel", Format(entry));
        }
        var completed = sink.Events.Last();
        Assert.Equal(LogEventLevel.Warning, completed.Level);
        using var json = JsonDocument.Parse(Format(completed));
        var properties = json.RootElement.GetProperty("Properties");
        Assert.Equal("HttpRequestCompleted", properties.GetProperty("EventName").GetString());
        Assert.Equal("failure", properties.GetProperty("Outcome").GetString());
        Assert.Equal("FORBIDDEN", properties.GetProperty("ErrorCode").GetString());
    }

    [Fact]
    public async Task FailedOperationIsTimedAndCorrelatedWithoutSerializingPayloadOrExceptionMessage()
    {
        var sink = new CaptureSink();
        using var logger = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
        using var factory = LoggerFactory.Create(builder => builder.AddSerilog(logger));
        var behavior = new LoggingBehavior<SecretRequest, string>(factory.CreateLogger<LoggingBehavior<SecretRequest, string>>());
        var failure = new ConflictException("TEST_CONFLICT", "credential-sentinel");
        using (factory.CreateLogger("Test").BeginScope(new Dictionary<string, object> { ["CorrelationId"] = "operation-123" }))
        {
            var thrown = await Assert.ThrowsAsync<ConflictException>(() => behavior.Handle(
                new SecretRequest("credential-sentinel"), _ => Task.FromException<string>(failure), default));
            Assert.Same(failure, thrown);
        }
        var entry = sink.Events.Last();
        Assert.Equal(LogEventLevel.Warning, entry.Level);
        Assert.Equal("TEST_CONFLICT", Scalar(entry, "ErrorCode"));
        Assert.Equal("operation-123", Scalar(entry, "CorrelationId"));
        Assert.Equal("SecretRequest", Scalar(entry, "OperationName"));
        Assert.IsType<double>(((ScalarValue)entry.Properties["ElapsedMilliseconds"]).Value);
        Assert.DoesNotContain("credential-sentinel", string.Join("\n", sink.Events.Select(Format)));
        factory.CreateLogger("Test").LogInformation("After operation");
        Assert.False(sink.Events.Last().Properties.ContainsKey("OperationName"));
    }

    [Fact]
    public async Task ClientCancellationDoesNotBecomeAnInternalServerFailure()
    {
        var sink = new CaptureSink();
        using var logger = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
        using var factory = LoggerFactory.Create(builder => builder.AddSerilog(logger));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        var middleware = new CorrelationIdMiddleware(_ => throw new OperationCanceledException(cancellation.Token),
            factory.CreateLogger<CorrelationIdMiddleware>());
        await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));
        var entry = Assert.Single(sink.Events);
        Assert.Equal("cancelled", Scalar(entry, "Outcome"));
        Assert.Equal("CLIENT_CLOSED_REQUEST", Scalar(entry, "ErrorCode"));
        Assert.Equal(499, ((ScalarValue)entry.Properties["StatusCode"]).Value);
        Assert.Equal(200, context.Response.StatusCode);
    }

    private sealed record SecretRequest(string Password) : MediatR.IRequest<string>;
    private static object? Scalar(LogEvent entry, string key) => ((ScalarValue)entry.Properties[key]).Value;
    private static string Format(LogEvent entry)
    {
        using var writer = new StringWriter();
        new SafeJsonFormatter().Format(entry, writer);
        return writer.ToString();
    }
    private sealed class CaptureSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
