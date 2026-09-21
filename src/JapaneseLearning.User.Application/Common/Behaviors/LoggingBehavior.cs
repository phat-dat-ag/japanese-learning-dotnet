using MediatR;
using Microsoft.Extensions.Logging;

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

        logger.LogInformation(
            "Handling {RequestName}",
            requestName);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // The API exception handler owns failure logging; expected business failures are not errors.
        var response = await next(cancellationToken);
        logger.LogInformation("Handled {RequestName} in {ElapsedMilliseconds}ms",
            requestName, stopwatch.ElapsedMilliseconds);
        return response;
    }
}
