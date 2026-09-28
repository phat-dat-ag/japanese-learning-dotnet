using JapaneseLearning.User.Application.Abstractions.Persistence;
using JapaneseLearning.User.Application.Abstractions.Security;
using JapaneseLearning.User.Application.Common.Exceptions;
using JapaneseLearning.User.Domain.Entities;
using MediatR;
using Microsoft.Extensions.Logging;

namespace JapaneseLearning.User.Application.Auth.Login;

public sealed class LoginCommandHandler
    : IRequestHandler<LoginCommand, LoginResponse>
{
    private readonly IUserRepository _userRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IRefreshTokenRepository _refreshTokenRepository;
    private readonly ITokenService _tokenService;

    private readonly ILogger<LoginCommandHandler> _logger;

    public LoginCommandHandler(
        IUserRepository userRepository,
        IPasswordHasher passwordHasher,
        IRefreshTokenRepository refreshTokenRepository,
        ITokenService tokenService,
        ILogger<LoginCommandHandler> logger)
    {
        _logger = logger;
        _userRepository = userRepository;
        _passwordHasher = passwordHasher;
        _refreshTokenRepository = refreshTokenRepository;
        _tokenService = tokenService;
    }

    public async Task<LoginResponse> Handle(
        LoginCommand request,
        CancellationToken cancellationToken)
    {
        var email = request.Email
            .Trim()
            .ToLowerInvariant();

        var user = await _userRepository.GetByEmailAsync(
            email,
            cancellationToken);

        if (user is null)
        {
            _logger.LogWarning(new EventId(2100, "AuthenticationRejected"),
                "Authentication operation {Operation} rejected: {Reason}", "Login", "INVALID_CREDENTIALS");

            throw new UnauthorizedException(
                "INVALID_CREDENTIALS",
                "Invalid email or password.");
        }

        if (!user.IsActive)
        {
            _logger.LogWarning(new EventId(2100, "AuthenticationRejected"),
                "Authentication operation {Operation} rejected: {Reason}", "Login", "USER_INACTIVE");

            throw new UnauthorizedException(
                "USER_INACTIVE",
                "User account is inactive.");
        }

        var passwordValid = _passwordHasher.Verify(
            request.Password,
            user.PasswordHash);

        if (!passwordValid)
        {
            _logger.LogWarning(new EventId(2100, "AuthenticationRejected"),
                "Authentication operation {Operation} rejected: {Reason}", "Login", "INVALID_CREDENTIALS");

            throw new UnauthorizedException(
                "INVALID_CREDENTIALS",
                "Invalid email or password.");
        }

        var tokens = _tokenService.CreateTokens(
            user.Id,
            user.Username,
            user.Email,
            user.Role);

        var refreshToken = new RefreshToken(
            Guid.NewGuid(),
            user.Id,
            _tokenService.HashRefreshToken(
                tokens.RefreshToken),
            tokens.RefreshTokenExpiresAt,
            DateTime.UtcNow);

        await _refreshTokenRepository.AddAsync(
            refreshToken,
            cancellationToken);

        _logger.LogInformation(new EventId(2001, "LoginSucceeded"), "LoginSucceeded for {UserId}", user.Id);

        return new LoginResponse(
            tokens.AccessToken,
            tokens.RefreshToken,
            tokens.AccessTokenExpiresIn);
    }
}