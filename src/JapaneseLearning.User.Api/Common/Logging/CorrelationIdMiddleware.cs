using System.Diagnostics;

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
            ["CorrelationId"] = correlationId
        });
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            // Do not log request URLs, headers, bodies, or exception messages.
            logger.LogInformation("HTTP request completed with status {StatusCode} in {ElapsedMilliseconds}ms",
                context.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private static bool IsValid(string? value) =>
        value is { Length: >= 1 and <= 64 } &&
        value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-');
}
