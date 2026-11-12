using MediatR;
using Microsoft.Extensions.Logging;
using JapaneseLearning.User.Application.Common.Exceptions;
using System.Diagnostics;

namespace JapaneseLearning.User.Application.Common.Behaviors;

public sealed class LoggingBehavior<TRequest, TResponse>(
    ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["OperationName"] = requestName,
            ["RequestType"] = typeof(TRequest).FullName
        });

        logger.LogInformation(new EventId(1100, "ApplicationOperationStarted"),
            "Handling {RequestName}",
            requestName);

        var started = Stopwatch.GetTimestamp();

        // The API exception handler owns failure logging; expected business failures are not errors.
        try
        {
            var response = await next(cancellationToken);
            using var outcome = logger.BeginScope(new Dictionary<string, object> { ["Outcome"] = "success" });
            logger.LogInformation(new EventId(1101, "ApplicationOperationCompleted"),
                "Handled {RequestName} in {ElapsedMilliseconds}ms",
                requestName, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            using var outcome = logger.BeginScope(new Dictionary<string, object> { ["Outcome"] = "cancelled" });
            logger.LogWarning(new EventId(1103, "ApplicationOperationCancelled"),
                "Cancelled {RequestName} after {ElapsedMilliseconds}ms",
                requestName, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
        catch (Exception exception)
        {
            using var outcome = logger.BeginScope(new Dictionary<string, object?>
            {
                ["Outcome"] = "failure",
                ["ExceptionType"] = exception.GetType().FullName,
                ["ErrorCode"] = exception is AppException app ? app.Code : "INTERNAL_SERVER_ERROR"
            });
            // Do not attach the exception or payload; the API owns safe stack diagnostics.
            logger.LogWarning(new EventId(1102, "ApplicationOperationFailed"),
                "Failed {RequestName} after {ElapsedMilliseconds}ms",
                requestName, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }
}
