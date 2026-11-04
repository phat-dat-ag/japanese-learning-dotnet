namespace JapaneseLearning.User.Api.Common.Errors;

public sealed record ApiError
{
    public string Code { get; }
    public string Message { get; }
    public IReadOnlyCollection<ApiErrorDetail> Details { get; }

    public ApiError(string code, string message, IReadOnlyCollection<ApiErrorDetail>? details = null)
    {
        Code = code;
        Message = message;
        Details = details ?? [];
    }
}
