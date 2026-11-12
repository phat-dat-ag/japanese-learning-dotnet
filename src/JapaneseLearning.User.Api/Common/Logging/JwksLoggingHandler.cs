using System.Diagnostics;

namespace JapaneseLearning.User.Api.Common.Logging;

// Only attached to the existing JWKS client. Never retries or inspects keys/tokens/bodies.
public sealed class JwksLoggingHandler(
    IHttpContextAccessor accessor, ILogger<JwksLoggingHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var correlation = accessor.HttpContext?.TraceIdentifier;

        if (CanonicalLogFields.ValidCorrelation(correlation))
        {
            request.Headers.Remove(CorrelationIdMiddleware.HeaderName);
            request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, correlation);
        }

        var started = Stopwatch.GetTimestamp();
        var outcome = "failure";
        int? status = null;
        string? errorType = null;

        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlation,
            ["OperationName"] = "jwks.fetch",
            ["DependencyName"] = "jwks-api",
            ["DependencyType"] = "http",
            ["DependencyOperation"] = "jwks.fetch"
        });

        try
        {
            var response = await base.SendAsync(request, cancellationToken);

            status = (int)response.StatusCode;
            outcome = response.IsSuccessStatusCode ? "success" : "failure";

            return response;
        }
        catch (OperationCanceledException exception)
        {
            outcome = cancellationToken.IsCancellationRequested ? "cancelled" : "timeout";
            errorType = exception.GetType().FullName;

            throw;
        }
        catch (HttpRequestException exception)
        {
            errorType = exception.GetType().FullName;

            throw;
        }
        finally
        {
            using var terminal = logger.BeginScope(new Dictionary<string, object?>
            {
                ["Outcome"] = outcome,
                ["StatusCode"] = status,
                ["ExceptionType"] = errorType,
                ["ErrorCategory"] = outcome == "cancelled" ? "cancellation"
                    : outcome == "timeout" ? "timeout" : "dependency"
            });

            logger.Log(outcome == "success" ? LogLevel.Information : LogLevel.Warning,
                new EventId(1200, "DependencyCompleted"),
                "JWKS retrieval completed in {ElapsedMilliseconds}ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}
