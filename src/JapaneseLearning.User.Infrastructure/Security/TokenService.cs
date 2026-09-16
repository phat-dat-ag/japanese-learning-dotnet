using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using JapaneseLearning.User.Application.Abstractions.Security;
using JapaneseLearning.User.Domain.Users;
using JapaneseLearning.User.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace JapaneseLearning.User.Infrastructure.Security;

public sealed class TokenService : ITokenService
{
    private readonly JwtOptions _options;
    private readonly JwtRsaKeys _keys;

    public TokenService(
        IOptions<JwtOptions> options,
        JwtRsaKeys keys)
    {
        _options = options.Value;
        _keys = keys;
    }

    public TokenResult CreateTokens(
        Guid userId,
        string username,
        string email,
        UserRole role)
    {
        var accessToken = GenerateAccessToken(
            userId,
            username,
            email,
            role);

        var refreshToken =
            GenerateRefreshToken();

        var refreshTokenExpiresAt =
            DateTime.UtcNow.AddDays(
                _options.RefreshTokenExpirationDays);

        return new TokenResult(
            accessToken,
            refreshToken,
            refreshTokenExpiresAt,
            _options.AccessTokenExpirationMinutes * 60);
    }

    public string HashRefreshToken(
        string refreshToken)
    {
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes(refreshToken));

        return Convert.ToHexString(bytes);
    }

    private string GenerateAccessToken(
        Guid userId,
        string username,
        string email,
        UserRole role)
    {
        var claims = new[]
        {
            new Claim(
                JwtRegisteredClaimNames.Sub,
                userId.ToString()),

            new Claim(
                JwtRegisteredClaimNames.UniqueName,
                username),

            new Claim(
                JwtRegisteredClaimNames.Email,
                email),

            new Claim(
                ClaimTypes.Role,
                role.ToString()),

            new Claim(
                JwtRegisteredClaimNames.Jti,
                Guid.NewGuid().ToString())
        };

        var credentials = new SigningCredentials(
            _keys.SigningKey,
            SecurityAlgorithms.RsaSha256);

        // Use the standard outbound mapping, including ClaimTypes.Role -> role.
        var handler = new JwtSecurityTokenHandler
        {
            SetDefaultTimesOnTokenCreation = false
        };
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(
                _options.AccessTokenExpirationMinutes),
            SigningCredentials = credentials
        });

        return handler.WriteToken(token);
    }

    private static string GenerateRefreshToken()
    {
        var bytes =
            RandomNumberGenerator.GetBytes(64);

        return Convert.ToBase64String(bytes);
    }
}