namespace JapaneseLearning.User.Infrastructure.Configuration;

public sealed class JwtSigningKeyOptions
{
    public string Kid { get; set; } = "";

    public string PrivateKeyPath { get; set; } = "";

    public bool Active { get; set; }
}
