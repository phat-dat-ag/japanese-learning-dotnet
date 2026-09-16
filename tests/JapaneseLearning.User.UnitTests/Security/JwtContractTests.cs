using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using JapaneseLearning.User.Api.Authentication;
using JapaneseLearning.User.Domain.Users;
using JapaneseLearning.User.Infrastructure;
using JapaneseLearning.User.Infrastructure.Configuration;
using JapaneseLearning.User.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace JapaneseLearning.User.UnitTests.Security;

public sealed class JwtContractTests
{
    // Test-only keys, unrelated to deployment credentials.
    private const string Secret = "jwt-contract-test-key-0123456789-abcdef";
    private const string OtherSecret = "different-test-key-0123456789-abcdefghi";
    private static readonly Guid UserId = Guid.Parse("db3d0876-c533-4ec3-90a2-b78102cb9fe7");

    [Theory]
    [InlineData(UserRole.User)]
    [InlineData(UserRole.Admin)]
    public void GeneratedTokenContainsExpectedContract(UserRole role)
    {
        var options = CreateOptions();
        var before = DateTime.UtcNow;
        var result = CreateService(options).CreateTokens(UserId, "learner", "learner@example.com", role);
        var after = DateTime.UtcNow;
        var token = new JwtSecurityTokenHandler().ReadJwtToken(result.AccessToken);

        Assert.Equal(UserId.ToString(), token.Subject);
        Assert.Equal("learner", token.Payload[JwtRegisteredClaimNames.UniqueName]);
        Assert.Equal("learner@example.com", token.Payload[JwtRegisteredClaimNames.Email]);
        Assert.Equal(role.ToString(), token.Payload["role"]);
        Assert.False(token.Payload.ContainsKey(ClaimTypes.Role));
        Assert.True(Guid.TryParse(token.Id, out _));
        Assert.Equal(options.Issuer, token.Issuer);
        Assert.Equal(options.Audience, Assert.Single(token.Audiences));
        Assert.Equal(SecurityAlgorithms.HmacSha256, token.Header.Alg);
        Assert.InRange(token.ValidTo, before.AddMinutes(15).AddSeconds(-1), after.AddMinutes(15));
        Assert.Equal(900, result.AccessTokenExpiresIn);
    }

    [Fact]
    public void NewlyIssuedTokensHaveDifferentIdentifiers()
    {
        var service = CreateService(CreateOptions());
        var handler = new JwtSecurityTokenHandler();
        var first = handler.ReadJwtToken(service.CreateTokens(UserId, "learner", "learner@example.com", UserRole.User).AccessToken);
        var second = handler.ReadJwtToken(service.CreateTokens(UserId, "learner", "learner@example.com", UserRole.User).AccessToken);

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task CustomConfigurationControlsIssuerAudienceAndLifetime()
    {
        var options = CreateOptions();
        options.Issuer = "Configured.Issuer";
        options.Audience = "Configured.Audience";
        options.AccessTokenExpirationMinutes = 3;
        var before = DateTime.UtcNow;
        var result = CreateService(options).CreateTokens(UserId, "learner", "learner@example.com", UserRole.User);
        var token = new JwtSecurityTokenHandler().ReadJwtToken(result.AccessToken);
        using var provider = CreateProvider(options);

        Assert.Equal(options.Issuer, token.Issuer);
        Assert.Equal(options.Audience, Assert.Single(token.Audiences));
        Assert.InRange(token.ValidTo, before.AddMinutes(3).AddSeconds(-1), DateTime.UtcNow.AddMinutes(3));
        Assert.Equal(180, result.AccessTokenExpiresIn);
        Assert.True((await Authenticate(provider, result.AccessToken)).Succeeded);
    }

    [Theory]
    [InlineData(UserRole.User)]
    [InlineData(UserRole.Admin)]
    public async Task ValidTokenPopulatesCurrentUserAndAuthorizesExpectedRole(UserRole role)
    {
        var options = CreateOptions();
        using var provider = CreateProvider(options);
        var token = CreateService(options).CreateTokens(UserId, "learner", "learner@example.com", role);
        var result = await Authenticate(provider, token.AccessToken);

        Assert.True(result.Succeeded);
        var principal = Assert.IsType<ClaimsPrincipal>(result.Principal);
        var currentUser = new CurrentUser(new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = principal }
        });
        Assert.True(currentUser.IsAuthenticated);
        Assert.Equal(UserId, currentUser.UserId);
        Assert.Equal("learner", currentUser.Username);
        Assert.Equal("learner@example.com", currentUser.Email);
        Assert.Equal(role.ToString(), currentUser.Role);
        Assert.Equal("learner", principal.Identity?.Name);

        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireRole(role.ToString())
            .Build();
        Assert.True((await authorization.AuthorizeAsync(principal, null, policy)).Succeeded);

        var otherRole = role == UserRole.User ? "Admin" : "User";
        var otherPolicy = new AuthorizationPolicyBuilder().RequireRole(otherRole).Build();
        Assert.False((await authorization.AuthorizeAsync(principal, null, otherPolicy)).Succeeded);
    }

    [Theory]
    [InlineData("expired", typeof(SecurityTokenExpiredException))]
    [InlineData("issuer", typeof(SecurityTokenInvalidIssuerException))]
    [InlineData("audience", typeof(SecurityTokenInvalidAudienceException))]
    [InlineData("signature", typeof(SecurityTokenSignatureKeyNotFoundException))]
    [InlineData("unsigned", typeof(SecurityTokenInvalidSignatureException))]
    [InlineData("expiration", typeof(SecurityTokenNoExpirationException))]
    public async Task InvalidTokensAreRejected(string scenario, Type expectedFailure)
    {
        using var provider = CreateProvider(CreateOptions());
        var result = await Authenticate(provider, CreateInvalidToken(scenario));

        Assert.False(result.Succeeded);
        Assert.Null(result.Principal);
        Assert.IsType(expectedFailure, result.Failure);
    }

    [Fact]
    public async Task PreviouslyIssuedDotNetRoleClaimRemainsSupported()
    {
        var options = CreateOptions();
        using var provider = CreateProvider(options);
        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, UserId.ToString()),
                new Claim(ClaimTypes.Role, "User")
            ],
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)),
                SecurityAlgorithms.HmacSha256));
        var result = await Authenticate(provider, new JwtSecurityTokenHandler().WriteToken(token));

        Assert.True(result.Succeeded);
        Assert.True(result.Principal!.IsInRole("User"));
    }

    [Fact]
    public void MissingHttpContextIsUnauthenticated()
    {
        var currentUser = new CurrentUser(new HttpContextAccessor());

        Assert.False(currentUser.IsAuthenticated);
        Assert.Null(currentUser.UserId);
        Assert.Null(currentUser.Username);
        Assert.Null(currentUser.Email);
        Assert.Null(currentUser.Role);
    }

    [Fact]
    public void MalformedUserIdReturnsNull()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "invalid-guid")], "Bearer"));
        var currentUser = new CurrentUser(new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = principal }
        });

        Assert.True(currentUser.IsAuthenticated);
        Assert.Null(currentUser.UserId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveAccessTokenLifetimeIsRejected(int minutes)
    {
        var options = CreateOptions();
        options.AccessTokenExpirationMinutes = minutes;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(CreateConfiguration(options));
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<JwtOptions>>().Value);
        Assert.Contains("JWT access-token lifetime must be positive.", exception.Failures);
    }

    private static JwtOptions CreateOptions() => new()
    {
        Secret = Secret,
        Issuer = "JapaneseLearning.User",
        Audience = "JapaneseLearning",
        AccessTokenExpirationMinutes = 15,
        RefreshTokenExpirationDays = 7
    };

    private static TokenService CreateService(JwtOptions options) =>
        new(Options.Create(options));

    private static IConfiguration CreateConfiguration(JwtOptions options) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Secret"] = options.Secret,
            ["Jwt:Issuer"] = options.Issuer,
            ["Jwt:Audience"] = options.Audience,
            ["Jwt:AccessTokenExpirationMinutes"] = options.AccessTokenExpirationMinutes.ToString(),
            ["Jwt:RefreshTokenExpirationDays"] = options.RefreshTokenExpirationDays.ToString()
        }).Build();

    private static ServiceProvider CreateProvider(JwtOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddJwtAuthentication(CreateConfiguration(options));
        services.AddAuthorization();
        return services.BuildServiceProvider();
    }

    private static async Task<AuthenticateResult> Authenticate(
        IServiceProvider provider, string token)
    {
        using var scope = provider.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Headers.Authorization = "Bearer " + token;
        return await context.AuthenticateAsync();
    }

    private static string CreateInvalidToken(string scenario)
    {
        var options = CreateOptions();
        var now = DateTime.UtcNow;
        var key = scenario == "signature" ? OtherSecret : Secret;
        var token = new JwtSecurityToken(
            issuer: scenario == "issuer" ? "Wrong.Issuer" : options.Issuer,
            audience: scenario == "audience" ? "Wrong.Audience" : options.Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, UserId.ToString()),
                new Claim("role", "User")
            ],
            notBefore: now.AddHours(-1),
            expires: scenario == "expiration" ? null :
                scenario == "expired" ? now.AddMinutes(-1) : now.AddMinutes(15),
            signingCredentials: scenario == "unsigned" ? null :
                new SigningCredentials(
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                    SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
