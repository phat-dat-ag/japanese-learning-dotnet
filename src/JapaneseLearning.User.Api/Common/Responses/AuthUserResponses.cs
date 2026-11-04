namespace JapaneseLearning.User.Api.Common.Responses;

public sealed record CurrentUserResponse(Guid? UserId, string? Username, string? Email, string? Role);

public sealed record AdminTestResponse(string Message, Guid? UserId, string? Role);
