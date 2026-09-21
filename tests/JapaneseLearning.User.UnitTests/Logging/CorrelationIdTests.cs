using System.Collections.Concurrent;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using JapaneseLearning.User.Api.Common.Errors;
using JapaneseLearning.User.Api.Common.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JapaneseLearning.User.UnitTests.Logging;

public sealed class CorrelationIdTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("valid_ID-123")]
    [InlineData("a")]
    [InlineData("invalid value")]
    [InlineData("invalid,duplicate")]
    [InlineData("invalid.jwt.value")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task ResponseAndErrorBodyUseTheSameSafeId(string? incoming)
    {
        await using var host = await TestHost.Start();
        foreach (var path in new[] { "/ok", "/error", "/missing", "/validation" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (incoming is not null)
                request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, incoming);
            using var response = await host.Client.SendAsync(request);
            var id = Assert.Single(response.Headers.GetValues(CorrelationIdMiddleware.HeaderName));
            var valid = incoming is { Length: >= 1 and <= 64 } &&
                incoming.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
            if (valid) Assert.Equal(incoming, id);
            else Assert.Matches("^[a-f0-9]{32}$", id);
            Assert.Equal(path == "/error" ? 500 : path == "/missing" ? 404 : path == "/validation" ? 400 : 200, (int)response.StatusCode);
            if (path is "/error" or "/validation")
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(id, body.RootElement.GetProperty("traceId").GetString());
            }
            else if (path == "/ok") Assert.Equal(id, await response.Content.ReadAsStringAsync());
            Assert.Contains(host.Logs.Entries, e => e.Contains(id) && e.Contains("HTTP request completed"));
        }
        Assert.DoesNotContain(host.Logs.Entries, e => e.Contains("credential-sentinel"));
    }

    [Fact]
    public async Task DuplicateHeadersAreReplacedAndConcurrentScopesStayIsolated()
    {
        await using var host = await TestHost.Start();
        using var duplicate = new HttpRequestMessage(HttpMethod.Get, "/ok");
        duplicate.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, new[] { "first", "second" });
        using var response = await host.Client.SendAsync(duplicate);
        Assert.Matches("^[a-f0-9]{32}$", Assert.Single(response.Headers.GetValues(CorrelationIdMiddleware.HeaderName)));

        await Task.WhenAll(Enumerable.Range(0, 16).Select(async index =>
        {
            var id = $"concurrent-{index}";
            using var request = new HttpRequestMessage(HttpMethod.Get, "/ok");
            request.Headers.Add(CorrelationIdMiddleware.HeaderName, id);
            using var result = await host.Client.SendAsync(request);
            Assert.Equal(id, await result.Content.ReadAsStringAsync());
            Assert.Equal(id, Assert.Single(result.Headers.GetValues(CorrelationIdMiddleware.HeaderName)));
            Assert.Contains(host.Logs.Entries, e => e.Contains($"CorrelationId={id}|") && e.Contains($"Application {id}"));
        }));
    }

    private sealed class TestHost(WebApplication app, HttpClient client, CapturingLogs logs) : IAsyncDisposable
    {
        public HttpClient Client => client;
        public CapturingLogs Logs => logs;

        public static async Task<TestHost> Start()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var logs = new CapturingLogs();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            // Production disables the framework's raw exception dump, using our safe handler instead.
            builder.Logging.AddFilter("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware", LogLevel.None);
            builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
            builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
                context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier);
            builder.Services.AddControllers().AddApplicationPart(typeof(CorrelationValidationController).Assembly);
            var app = builder.Build();
            app.UseMiddleware<CorrelationIdMiddleware>();
            app.UseExceptionHandler();
            app.MapControllers();
            app.MapGet("/ok", async (HttpContext context, ILogger<CorrelationIdTests> logger) =>
            {
                await Task.Delay(5);
                logger.LogInformation("Application {Id}", context.TraceIdentifier);
                return context.TraceIdentifier;
            });
            app.MapGet("/error", (HttpContext _) => Task.FromException(new InvalidOperationException("credential-sentinel")));
            await app.StartAsync();
            return new TestHost(app, new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)) }, logs);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class CapturingLogs : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();
        public ConcurrentQueue<string> Entries { get; } = new();
        public void SetScopeProvider(IExternalScopeProvider provider) => scopes = provider;
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);
        public void Dispose() { }

        private sealed class CapturingLogger(CapturingLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner.scopes.Push(state);
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var values = new List<string>();
                owner.scopes.ForEachScope((scope, list) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object>> pairs)
                        list.AddRange(pairs.Select(pair => $"{pair.Key}={pair.Value}|"));
                }, values);
                owner.Entries.Enqueue(string.Join("", values) + formatter(state, exception) + exception);
            }
        }
    }
}

[ApiController]
public sealed class CorrelationValidationController : ControllerBase
{
    [HttpGet("/validation")]
    public IActionResult Validate([FromQuery, Required] string value) => Ok();
}
