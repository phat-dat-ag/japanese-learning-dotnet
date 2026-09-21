using FluentValidation;
using JapaneseLearning.User.Application.Common.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using JapaneseLearning.User.Api.Common.Responses;

namespace JapaneseLearning.User.Api.Common.Errors;

public sealed class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var traceId = httpContext.TraceIdentifier;

        var (statusCode, error) = exception switch
        {
            NotFoundException ex => (
                StatusCodes.Status404NotFound,
                new ApiError(ex.Code, ex.Message)),

            ConflictException ex => (
                StatusCodes.Status409Conflict,
                new ApiError(ex.Code, ex.Message)),

            UnauthorizedException ex => (
                StatusCodes.Status401Unauthorized,
                new ApiError(ex.Code, ex.Message)),

            ForbiddenException ex => (
                StatusCodes.Status403Forbidden,
                new ApiError(ex.Code, ex.Message)),

            ValidationException ex => (
                StatusCodes.Status400BadRequest,
                new ApiError(
                    "VALIDATION_ERROR",
                    "One or more validation errors occurred.",
                    ex.Errors
                        .GroupBy(x => x.PropertyName)
                        .Select(g => new ApiErrorDetail(
                            g.Key,
                            string.Join("; ", g.Select(x => x.ErrorMessage))))
                        .ToArray())),

            BadHttpRequestException ex => (ex.StatusCode, ApiErrorHandling.ForStatus(ex.StatusCode)),

            _ => (
                StatusCodes.Status500InternalServerError,
                new ApiError(
                    "INTERNAL_SERVER_ERROR",
                    "An unexpected error occurred."))
        };

        if (statusCode >= 500)
        {
            // Metadata only: exception messages, data and source file paths can contain secrets.
            var locations = new List<string>();
            for (Exception? cause = exception; cause is not null && locations.Count < 5; cause = cause.InnerException)
            {
                var frames = new System.Diagnostics.StackTrace(cause, false).GetFrames();
                locations.Add(cause.GetType().FullName + ": " + string.Join(" <- ",
                    frames.Take(12).Select(frame =>
                        frame.GetMethod()?.DeclaringType?.FullName + "." + frame.GetMethod()?.Name)));
            }
            logger.LogError("Unhandled exception {ExceptionType}. Locations: {FailureLocations}. TraceId: {TraceId}",
                exception.GetType().Name, string.Join(" | ", locations), traceId);
        }

        var response = ApiResponse<object>.Fail(
            error,
            traceId);

        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/json";

        await httpContext.Response.WriteAsJsonAsync(
            response,
            cancellationToken);

        return true;
    }
}