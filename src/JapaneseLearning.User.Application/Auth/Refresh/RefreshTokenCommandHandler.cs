using JapaneseLearning.User.Application.Abstractions.Persistence;
using JapaneseLearning.User.Application.Abstractions.Security;
using JapaneseLearning.User.Application.Common.Exceptions;
using JapaneseLearning.User.Domain.Entities;
using MediatR;
using Microsoft.Extensions.Logging;

namespace JapaneseLearning.User.Application.Auth.Refresh;

public sealed class RefreshTokenCommandHandler
    : IRequestHandler<
        RefreshTokenCommand,
        RefreshTokenResponse>
{
    private readonly IRefreshTokenRepository _refreshTokenRepository;
    private readonly IUserRepository _userRepository;
    private readonly ITokenService _tokenService;

    private readonly ILogger<RefreshTokenCommandHandler> _logger;

    public RefreshTokenCommandHandler(
        IRefreshTokenRepository refreshTokenRepository,
        IUserRepository userRepository,
        ITokenService tokenService,
        ILogger<RefreshTokenCommandHandler> logger)
    {
        _logger = logger;
        _refreshTokenRepository = refreshTokenRepository;
        _userRepository = userRepository;
        _tokenService = tokenService;
    }

    public async Task<RefreshTokenResponse> Handle(
        RefreshTokenCommand request,
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
                "Authentication operation {Operation} rejected: {Reason}", "Refresh", "INVALID_REFRESH_TOKEN");

            throw new UnauthorizedException(
                "INVALID_REFRESH_TOKEN",
                "Refresh token is invalid.");
        }

        if (existingToken.RevokedAt.HasValue)
        {
            _logger.LogWarning(new EventId(2100, "AuthenticationRejected"),
                "Authentication operation {Operation} rejected: {Reason}", "Refresh", "REFRESH_TOKEN_REVOKED");

            throw new UnauthorizedException(
                "REFRESH_TOKEN_REVOKED",
                "Refresh token has been revoked.");
        }

        if (existingToken.ExpiresAt <= DateTime.UtcNow)
        {
            _logger.LogWarning(new EventId(2100, "AuthenticationRejected"),
                "Authentication operation {Operation} rejected: {Reason}", "Refresh", "REFRESH_TOKEN_EXPIRED");

            throw new UnauthorizedException(
                "REFRESH_TOKEN_EXPIRED",
                "Refresh token has expired.");
        }

        var user = await _userRepository.GetByIdAsync(
            existingToken.UserId,
            cancellationToken);

        if (user is null || !user.IsActive)
        {
            _logger.LogWarning(new EventId(2100, "AuthenticationRejected"),
                "Authentication operation {Operation} rejected: {Reason}", "Refresh", "INVALID_REFRESH_TOKEN");

            throw new UnauthorizedException(
                "INVALID_REFRESH_TOKEN",
                "Refresh token is invalid.");
        }

        var tokens = _tokenService.CreateTokens(
            user.Id,
            user.Username,
            user.Email,
            user.Role);

        await _refreshTokenRepository.RevokeAsync(
            existingToken.Id,
            DateTime.UtcNow,
            cancellationToken);

        var newRefreshToken = new RefreshToken(
            Guid.NewGuid(),
            user.Id,
            _tokenService.HashRefreshToken(
                tokens.RefreshToken),
            tokens.RefreshTokenExpiresAt,
            DateTime.UtcNow);

        await _refreshTokenRepository.AddAsync(
            newRefreshToken,
            cancellationToken);

        _logger.LogInformation(new EventId(2003, "RefreshTokenRotated"), "RefreshTokenRotated for {UserId}", user.Id);

        return new RefreshTokenResponse(
            tokens.AccessToken,
            tokens.RefreshToken,
            tokens.AccessTokenExpiresIn);
    }
}