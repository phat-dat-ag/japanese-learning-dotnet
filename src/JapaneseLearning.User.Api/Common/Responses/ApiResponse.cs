using JapaneseLearning.User.Api.Common.Errors;
using System.Text.Json.Serialization;

namespace JapaneseLearning.User.Api.Common.Responses;

public sealed record ApiResponse<T>(
    bool Success,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] T? Data,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ApiError? Error,
    ResponseMeta Meta)
{
    public static ApiResponse<T> Ok(
        T data,
        HttpContext context)
        => new(
            Success: true,
            Data: data,
            Error: null,
            Meta: ResponseMeta.Create(context));

    public static ApiResponse<T> Fail(
        ApiError error,
        HttpContext context)
        => new(
            Success: false,
            Data: default,
            Error: error,
            Meta: ResponseMeta.Create(context));
}
