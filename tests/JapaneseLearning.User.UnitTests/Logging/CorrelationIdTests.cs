using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Net.Http.Headers;
using System.Text;
using JapaneseLearning.User.Api.Authentication;
using JapaneseLearning.User.Infrastructure.Configuration;
using JapaneseLearning.User.UnitTests.Security;
using Microsoft.IdentityModel.Tokens;
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

public sealed class CorrelationIdTests(RsaKeyFixture fixture) : IClassFixture<RsaKeyFixture>
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
        await using var host = await TestHost.Start(fixture);
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
            if (path is "/error" or "/validation" or "/missing")
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
        await using var host = await TestHost.Start(fixture);
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

    [Theory]
    [InlineData("/missing", 404, "NOT_FOUND")]
    [InlineData("/conflict", 409, "TEST_CONFLICT")]
    [InlineData("/business-unauthorized", 401, "INVALID_CREDENTIALS")]
    [InlineData("/business-forbidden", 403, "FORBIDDEN")]
    [InlineData("/business-missing", 404, "TEST_NOT_FOUND")]
    [InlineData("/fluent-validation", 400, "VALIDATION_ERROR")]
    [InlineData("/validation", 400, "VALIDATION_ERROR")]
    [InlineData("/error", 500, "INTERNAL_SERVER_ERROR")]
    public async Task ErrorsAreSafeCorrelatedAndLoggedOnce(string path, int status, string code)
    {
        await using var host = await TestHost.Start(fixture);
        host.Client.DefaultRequestHeaders.Add("X-Correlation-ID", "error-contract");
        using var response = await host.Client.GetAsync(path);
        await AssertError(response, status, code);
        var errors = host.Logs.Entries.Where(line => line.StartsWith("Error|")).ToArray();
        if (status == 500)
        {
            var error = Assert.Single(errors);
            Assert.Contains("InvalidOperationException", error);
            Assert.Contains("CorrelationIdTests", error);
            Assert.Contains("error-contract", error);
        }
        else Assert.Empty(errors);
        Assert.DoesNotContain(host.Logs.Entries, line => line.Contains("credential-sentinel"));
    }

    [Fact]
    public async Task BindingAndMethodErrorsPreserveStatusWithoutReflectingInput()
    {
        await using var host = await TestHost.Start(fixture);
        host.Client.DefaultRequestHeaders.Add("X-Correlation-ID", "error-contract");
        using var malformed = await host.Client.PostAsync("/error-body",
            new StringContent("{\"count\":\"credential-sentinel\"}", Encoding.UTF8, "application/json"));
        await AssertError(malformed, 400, "VALIDATION_ERROR");
        using var method = await host.Client.PostAsync("/ok", null);
        await AssertError(method, 405, "METHOD_NOT_ALLOWED");
        Assert.Contains("GET", method.Content.Headers.Allow);
    }

    [Fact]
    public async Task AuthenticationKeepsChallengesAndRoleDecisions()
    {
        await using var host = await TestHost.Start(fixture);
        host.Client.DefaultRequestHeaders.Add("X-Correlation-ID", "error-contract");
        foreach (var token in new string?[] { null, "credential-sentinel", Token("User", expired: true) })
        {
            host.Client.DefaultRequestHeaders.Authorization = token is null ? null : new AuthenticationHeaderValue("Bearer", token);
            using var response = await host.Client.GetAsync("/protected");
            await AssertError(response, 401, "UNAUTHORIZED");
            Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).ToString());
        }
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("User"));
        using var allowed = await host.Client.GetAsync("/protected");
        Assert.Equal(200, (int)allowed.StatusCode);
        using var forbidden = await host.Client.GetAsync("/admin");
        await AssertError(forbidden, 403, "FORBIDDEN");
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("Admin"));
        using var admin = await host.Client.GetAsync("/admin");
        Assert.Equal(200, (int)admin.StatusCode);
        Assert.DoesNotContain(host.Logs.Entries, line => line.Contains("credential-sentinel"));
    }

    private string Token(string role, bool expired = false) => new JwtSecurityTokenHandler().WriteToken(
        new JwtSecurityToken("test-issuer", "test-audience", new[] { new Claim("role", role) },
            expires: DateTime.UtcNow.AddMinutes(expired ? -1 : 5),
            signingCredentials: new SigningCredentials(fixture.Keys.SigningKey, SecurityAlgorithms.RsaSha256)));

    private static async Task AssertError(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("error-contract", Assert.Single(response.Headers.GetValues("X-Correlation-ID")));
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("credential-sentinel", text);
        Assert.DoesNotContain("Exception", text);
        using var body = JsonDocument.Parse(text);
        Assert.False(body.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(code, body.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("error-contract", body.RootElement.GetProperty("traceId").GetString());
    }

    private sealed class TestHost(WebApplication app, HttpClient client, CapturingLogs logs) : IAsyncDisposable
    {
        public HttpClient Client => client;
        public CapturingLogs Logs => logs;

        public static async Task<TestHost> Start(RsaKeyFixture fixture)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var logs = new CapturingLogs();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            // Production disables the framework's raw exception dump, using our safe handler instead.
            builder.Logging.AddFilter("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware", LogLevel.None);
            builder.Services.AddSingleton(fixture.Keys);
            builder.Services.Configure<JwtOptions>(options =>
            {
                options.Issuer = "test-issuer";
                options.Audience = "test-audience";
            });
            builder.Services.AddJwtAuthentication();
            builder.Services.AddAuthorization();
            builder.Services.AddApiErrorHandling();
            builder.Services.AddControllers().AddApplicationPart(typeof(CorrelationValidationController).Assembly);
            var app = builder.Build();
            app.UseMiddleware<CorrelationIdMiddleware>();
            app.UseApiErrorHandling();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();
            app.MapGet("/protected", () => "ok").RequireAuthorization();
            app.MapGet("/admin", () => "ok").RequireAuthorization(policy => policy.RequireRole("Admin"));
            app.MapGet("/conflict", (HttpContext _) => Task.FromException(
                new JapaneseLearning.User.Application.Common.Exceptions.ConflictException("TEST_CONFLICT", "Already exists.")));
            app.MapGet("/business-unauthorized", (HttpContext _) => Task.FromException(
                new JapaneseLearning.User.Application.Common.Exceptions.UnauthorizedException("INVALID_CREDENTIALS", "Invalid credentials.")));
            app.MapGet("/business-forbidden", (HttpContext _) => Task.FromException(
                new JapaneseLearning.User.Application.Common.Exceptions.ForbiddenException("FORBIDDEN", "Access is forbidden.")));
            app.MapGet("/business-missing", (HttpContext _) => Task.FromException(
                new JapaneseLearning.User.Application.Common.Exceptions.NotFoundException("TEST_NOT_FOUND", "Resource not found.")));
            app.MapGet("/fluent-validation", (HttpContext _) => Task.FromException(
                new FluentValidation.ValidationException(new[] {
                    new FluentValidation.Results.ValidationFailure("email", "Email is invalid.") { AttemptedValue = "credential-sentinel" }
                })));

            app.MapGet("/ok", async (HttpContext context, ILogger<CorrelationIdTests> logger) =>
            {
                await Task.Delay(5);
                logger.LogInformation("Application {Id}", context.TraceIdentifier);
                return context.TraceIdentifier;
            });
            app.MapGet("/error", async (HttpContext _) =>
            {
                await Task.Delay(1);
                throw new InvalidOperationException("credential-sentinel");
            });
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
                owner.Entries.Enqueue(level + "|" + string.Join("", values) + formatter(state, exception) + exception);
            }
        }
    }
}

[ApiController]
public sealed class CorrelationValidationController : ControllerBase
{
    public sealed record Input(int Count);

    [HttpPost("/error-body")]
    public IActionResult Body([FromBody] Input input) => Ok();

    [HttpGet("/validation")]
    public IActionResult Validate([FromQuery, Required] string value) => Ok();
}
