namespace JapaneseLearning.User.Infrastructure.Configuration;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public List<JwtSigningKeyOptions> SigningKeys { get; set; } = [];

    public string JwksUrl { get; set; } = null!;

    public bool RequireHttpsMetadata { get; set; } = true;

    public string Issuer { get; set; } = null!;

    public string Audience { get; set; } = null!;

    public int AccessTokenExpirationMinutes { get; set; }

    public int RefreshTokenExpirationDays { get; set; }
}
