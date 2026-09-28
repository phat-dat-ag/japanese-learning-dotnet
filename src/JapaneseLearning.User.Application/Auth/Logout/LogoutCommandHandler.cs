using JapaneseLearning.User.Application.Abstractions.Persistence;
using JapaneseLearning.User.Application.Abstractions.Security;
using JapaneseLearning.User.Application.Common.Exceptions;
using MediatR;
using Microsoft.Extensions.Logging;

namespace JapaneseLearning.User.Application.Auth.Logout;

public sealed class LogoutCommandHandler
    : IRequestHandler<LogoutCommand>
{
    private readonly IRefreshTokenRepository _refreshTokenRepository;
    private readonly ITokenService _tokenService;

    private readonly ILogger<LogoutCommandHandler> _logger;

    public LogoutCommandHandler(
        IRefreshTokenRepository refreshTokenRepository,
        ITokenService tokenService,
        ILogger<LogoutCommandHandler> logger)
    {
        _logger = logger;
        _refreshTokenRepository = refreshTokenRepository;
        _tokenService = tokenService;
    }

    public async Task Handle(
        LogoutCommand request,
        CancellationToken cancellationToken)
    {
        var tokenHash =
            _tokenService.HashRefreshToken(
                request.RefreshToken);

        var existingToken =
            await _refreshTokenRepository.GetByTokenHashAsync(
                tokenHash,
                cancellationToken);

        if (existingToken is null)
        {
            _logger.LogWarning(new EventId(2100, "AuthenticationRejected"),
                "Authentication operation {Operation} rejected: {Reason}", "Logout", "INVALID_REFRESH_TOKEN");

            throw new UnauthorizedException(
                "INVALID_REFRESH_TOKEN",
                "Refresh token is invalid.");
        }

        if (existingToken.RevokedAt.HasValue)
        {
            _logger.LogWarning(new EventId(2100, "AuthenticationRejected"),
                "Authentication operation {Operation} rejected: {Reason}", "Logout", "REFRESH_TOKEN_REVOKED");

            throw new UnauthorizedException(
                "REFRESH_TOKEN_REVOKED",
                "Refresh token has already been revoked.");
        }

        if (existingToken.ExpiresAt <= DateTime.UtcNow)
        {
            _logger.LogWarning(new EventId(2100, "AuthenticationRejected"),
                "Authentication operation {Operation} rejected: {Reason}", "Logout", "REFRESH_TOKEN_EXPIRED");

            throw new UnauthorizedException(
                "REFRESH_TOKEN_EXPIRED",
                "Refresh token has expired.");
        }

        await _refreshTokenRepository.RevokeAsync(
            existingToken.Id,
            DateTime.UtcNow,
            cancellationToken);

        _logger.LogInformation(new EventId(2004, "LogoutSucceeded"), "LogoutSucceeded for {UserId}", existingToken.UserId);
    }
}