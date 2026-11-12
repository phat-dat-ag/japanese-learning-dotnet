using JapaneseLearning.User.Api.Common.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.Net;
using System.Text.Json;

namespace JapaneseLearning.User.UnitTests.Logging;

public sealed class JwksLoggingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PropagatesOnlyCorrelationAndPreservesResponseOrException(bool fail)
    {
        var sink = new Capture();

        using var log = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
        using var factory = LoggerFactory.Create(builder => builder.AddSerilog(log));

        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };

        accessor.HttpContext.TraceIdentifier = "request-123";

        var expected = new HttpResponseMessage(HttpStatusCode.OK);
        var failure = new HttpRequestException("database-password-sentinel");

        using var handler = new JwksLoggingHandler(accessor, factory.CreateLogger<JwksLoggingHandler>())
        {
            InnerHandler = new Probe(request =>
            {
                Assert.Equal("request-123", Assert.Single(request.Headers.GetValues("X-Correlation-ID")));
                Assert.False(request.Headers.Contains("Authorization"));
                Assert.Equal("?private=query-sentinel", request.RequestUri!.Query);

                return fail ? Task.FromException<HttpResponseMessage>(failure) : Task.FromResult(expected);
            })
        };

        using var client = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://test.invalid/jwks?private=query-sentinel");

        if (fail)
        {
            var actual = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request, default));
            Assert.Same(failure, actual);
        }
        else Assert.Same(expected, await client.SendAsync(request, default));

        var entry = Assert.Single(sink.Events);

        using var writer = new StringWriter();

        new SafeJsonFormatter().Format(entry, writer);

        using var json = JsonDocument.Parse(writer.ToString());

        Assert.Equal("dependency.completed", json.RootElement.GetProperty("event_name").GetString());
        Assert.Equal("jwks-api", json.RootElement.GetProperty("dependency_name").GetString());
        Assert.Equal("jwks.fetch", json.RootElement.GetProperty("dependency_operation").GetString());
        Assert.Equal("request-123", json.RootElement.GetProperty("correlation_id").GetString());
        Assert.DoesNotContain("sentinel", writer.ToString());
        factory.CreateLogger("after").LogInformation("After dependency");
        Assert.False(sink.Events.Last().Properties.ContainsKey("DependencyName"));
        accessor.HttpContext = null;
    }

    private sealed class Probe(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request);
    }

    private sealed class Capture : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
