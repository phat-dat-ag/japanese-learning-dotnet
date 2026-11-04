using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;

namespace JapaneseLearning.User.Api.Common.Logging;

public sealed class CorrelationIdMiddleware(
    RequestDelegate next,
    ILogger<CorrelationIdMiddleware> logger,
    IConfiguration? configuration = null)
{
    public const string HeaderName = "X-Correlation-ID";
    public const string CallerServiceHeaderName = "X-Caller-Service";
    public const string ErrorCodeKey = "Logging.ErrorCode";
    public const string ExceptionTypeKey = "Logging.ExceptionType";
    public const string TraceIdKey = "Logging.TraceId";
    public const string DistributedTraceHeaderName = "X-Distributed-Trace-Id";
    private readonly int _slowRequestMilliseconds = ReadSlowThreshold(configuration);

    private static int ReadSlowThreshold(IConfiguration? configuration)
    {
        var value = configuration?.GetValue<int>("Logging:SlowRequestThresholdMilliseconds", 1000) ?? 1000;
        return value > 0 ? value : throw new InvalidOperationException(
            "Logging:SlowRequestThresholdMilliseconds must be positive."
        );
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var values = context.Request.Headers[HeaderName];
        var candidate = values.Count == 1 ? values[0] : null;
        var correlationId = IsValid(candidate) ? candidate! : Guid.NewGuid().ToString("N");

        var traceId = Activity.Current?.TraceId is { } trace && trace != default
            ? trace.ToString() : ActivityTraceId.CreateRandom().ToString();
        context.Items[TraceIdKey] = traceId;
        // TraceIdentifier remains the validated correlation ID for existing callers.
        var requestId = context.TraceIdentifier;
        
        context.TraceIdentifier = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            context.Response.Headers[DistributedTraceHeaderName] = traceId;
            return Task.CompletedTask;
        });

        var endpoint = context.GetEndpoint();
        var action = endpoint?.Metadata.GetMetadata<ControllerActionDescriptor>();
        var route = (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? "/unmatched";
        var callerValues = context.Request.Headers[CallerServiceHeaderName];
        var claimedCaller = callerValues.Count == 1 && IsValid(callerValues[0]) ? callerValues[0]! : "unknown";
        
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId,
            ["TraceId"] = traceId,
            ["SpanId"] = Activity.Current?.SpanId.ToString() ?? string.Empty,
            ["ParentSpanId"] = Activity.Current?.ParentSpanId.ToString() ?? string.Empty,
            ["RequestId"] = requestId,
            ["ConnectionId"] = context.Connection.Id,
            ["RequestMethod"] = context.Request.Method,
            ["RequestRoute"] = route,
            ["Controller"] = action?.ControllerName,
            ["Action"] = action?.ActionName,
            ["HandlerMethod"] = action?.MethodInfo.Name,
            ["HandlerType"] = action?.ControllerTypeInfo.FullName,
            ["NetworkProtocol"] = context.Request.Protocol,
            ["RequestScheme"] = context.Request.Scheme,
            ["PeerAddress"] = context.Connection.RemoteIpAddress?.ToString(),
            ["PeerPort"] = context.Connection.RemotePort,
            // This header is caller-supplied metadata, never an authentication decision.
            ["ClaimedCallerService"] = claimedCaller
        });

        var started = Stopwatch.GetTimestamp();
        var failed = false;
        var cancelled = false;

        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            cancelled = true;
            throw;
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            cancelled |= context.RequestAborted.IsCancellationRequested;
            var status = cancelled ? 499 : failed ? StatusCodes.Status500InternalServerError : context.Response.StatusCode;
            var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            // The central handler owns the single ERROR diagnostic; summaries use WARN for failures.
            var level = status >= 400 || duration >= _slowRequestMilliseconds ? LogLevel.Warning : LogLevel.Information;
            // Query strings, headers and bodies are deliberately excluded.
            var userId = context.User.Identity?.IsAuthenticated == true &&
                Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
                    ? (Guid?)id : null;
           
            using var completion = logger.BeginScope(new Dictionary<string, object?>
            {
                ["Outcome"] = cancelled ? "cancelled" : status >= 400 ? "failure" : "success",
                ["ErrorCode"] = cancelled ? "CLIENT_CLOSED_REQUEST" : context.Items[ErrorCodeKey] ?? (status >= 400 ? Errors.ApiErrorHandling.ForStatus(status).Code : null),
                ["ExceptionType"] = context.Items[ExceptionTypeKey],
                ["IsAuthenticated"] = context.User.Identity?.IsAuthenticated == true,
                ["ResponseStarted"] = context.Response.HasStarted,
                ["IsSlow"] = duration >= _slowRequestMilliseconds,
                ["SlowRequestThresholdMilliseconds"] = _slowRequestMilliseconds,
                ["RequestContentLength"] = context.Request.ContentLength,
                ["ResponseContentLength"] = context.Response.ContentLength
            });
            
            logger.Log(level, new EventId(1000, "HttpRequestCompleted"),
                "HTTP request completed: {RequestMethod} {RequestPath} with status {StatusCode} in {ElapsedMilliseconds}ms for {UserId}",
                context.Request.Method, route, status, duration, userId);
        }
    }

    private static bool IsValid(string? value) =>
        value is { Length: >= 1 and <= 64 } &&
        value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-');
}
