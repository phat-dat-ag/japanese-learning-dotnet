using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace JapaneseLearning.User.Api.Authentication;

// Adapt the standalone JWKS contract to JwtBearer's supported configuration manager.
// No discovery request or issuer value from the remote service is needed.
public sealed class JwksConfigurationRetriever : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
        string address, IDocumentRetriever retriever, CancellationToken cancel)
    {
        var document = await retriever.GetDocumentAsync(address, cancel);
        var jwks = new JsonWebKeySet(document);
        var configuration = new OpenIdConnectConfiguration();
        foreach (var key in jwks.GetSigningKeys())
            configuration.SigningKeys.Add(key);

        if (configuration.SigningKeys.Count == 0)
            throw new InvalidOperationException("JWKS contains no usable signing keys.");

        return configuration;
    }
}
