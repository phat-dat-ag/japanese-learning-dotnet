using JapaneseLearning.User.Api.Common.Responses;
using Microsoft.AspNetCore.Mvc;

namespace JapaneseLearning.User.Api.Common.Errors;

public static class ApiErrorHandling
{
    public static IServiceCollection AddApiErrorHandling(this IServiceCollection services)
    {
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.PostConfigure<ApiBehaviorOptions>(options =>
        {
            options.SuppressMapClientErrors = true;
            options.InvalidModelStateResponseFactory = context => new BadRequestObjectResult(
                ApiResponse<object>.Fail(new ApiError("VALIDATION_ERROR",
                    "One or more validation errors occurred."), context.HttpContext.TraceIdentifier));
        });
        return services;
    }

    public static IApplicationBuilder UseApiErrorHandling(this IApplicationBuilder app)
    {
        app.UseExceptionHandler(_ => { });
        return app.UseStatusCodePages(async context =>
        {
            var http = context.HttpContext;
            await http.Response.WriteAsJsonAsync(
                ApiResponse<object>.Fail(ForStatus(http.Response.StatusCode), http.TraceIdentifier),
                http.RequestAborted);
        });
    }

    public static ApiError ForStatus(int status) => status switch
    {
        400 => new("BAD_REQUEST", "Bad request."),
        401 => new("UNAUTHORIZED", "Authentication is required."),
        403 => new("FORBIDDEN", "Access is forbidden."),
        404 => new("NOT_FOUND", "Resource not found."),
        405 => new("METHOD_NOT_ALLOWED", "Method not allowed."),
        409 => new("CONFLICT", "The request conflicts with the current state."),
        413 => new("PAYLOAD_TOO_LARGE", "Request body is too large."),
        415 => new("UNSUPPORTED_MEDIA_TYPE", "Unsupported media type."),
        429 => new("TOO_MANY_REQUESTS", "Too many requests."),
        >= 500 => new("INTERNAL_SERVER_ERROR", "An unexpected error occurred."),
        _ => new("HTTP_ERROR", "HTTP request failed.")
    };
}
