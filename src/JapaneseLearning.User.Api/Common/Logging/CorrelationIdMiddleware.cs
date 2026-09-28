using System.Diagnostics;
using System.Security.Claims;

namespace JapaneseLearning.User.Api.Common.Logging;

public sealed class CorrelationIdMiddleware(
    RequestDelegate next,
    ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context)
    {
        var values = context.Request.Headers[HeaderName];
        var candidate = values.Count == 1 ? values[0] : null;
        var correlationId = IsValid(candidate) ? candidate! : Guid.NewGuid().ToString("N");

        // Existing response/error contracts already expose TraceIdentifier.
        context.TraceIdentifier = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["TraceId"] = Activity.Current?.TraceId.ToString() ?? string.Empty,
            ["SpanId"] = Activity.Current?.SpanId.ToString() ?? string.Empty
        });

        var started = Stopwatch.GetTimestamp();
        var failed = false;

        try
        {
            await next(context);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            var status = failed ? StatusCodes.Status500InternalServerError : context.Response.StatusCode;
            // The exception handler owns error events; 401/403 are useful security warnings.
            var level = status is 401 or 403 ? LogLevel.Warning : LogLevel.Information;
            // Query strings, headers and bodies are deliberately excluded.
            var userId = context.User.Identity?.IsAuthenticated == true &&
                Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
                    ? (Guid?)id : null;
            logger.Log(level, new EventId(1000, "HttpRequestCompleted"),
                "HTTP request completed: {RequestMethod} {RequestPath} with status {StatusCode} in {ElapsedMilliseconds}ms for {UserId}",
                context.Request.Method, context.Request.Path.Value, status,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, userId);
        }
    }

    private static bool IsValid(string? value) =>
        value is { Length: >= 1 and <= 64 } &&
        value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-');
}
