namespace JapaneseLearning.User.Infrastructure.Configuration;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string PrivateKeyPath { get; set; } = null!;

    public string PublicKeyPath { get; set; } = null!;

    public string KeyId { get; set; } = null!;

    public string JwksUrl { get; set; } = null!;

    public bool RequireHttpsMetadata { get; set; } = true;

    public string Issuer { get; set; } = null!;

    public string Audience { get; set; } = null!;

    public int AccessTokenExpirationMinutes { get; set; }

    public int RefreshTokenExpirationDays { get; set; }
}
