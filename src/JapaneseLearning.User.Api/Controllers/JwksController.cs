using JapaneseLearning.User.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JapaneseLearning.User.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route(".well-known/jwks.json")]
public sealed class JwksController(JwtRsaKeys keys) : ControllerBase
{
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Get()
    {
        var key = keys.GetPublicJsonWebKey();
        // Explicitly allow only public JWK fields in the HTTP contract.
        return Ok(new
        {
            keys = new[]
            {
                new
                {
                    kty = key.Kty,
                    use = key.Use,
                    kid = key.Kid,
                    alg = key.Alg,
                    n = key.N,
                    e = key.E
                }
            }
        });
    }
}
