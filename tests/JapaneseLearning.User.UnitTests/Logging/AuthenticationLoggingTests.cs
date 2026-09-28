using JapaneseLearning.User.Api.Common.Logging;
using JapaneseLearning.User.Application.Abstractions.Persistence;
using JapaneseLearning.User.Application.Abstractions.Security;
using JapaneseLearning.User.Application.Auth.Login;
using JapaneseLearning.User.Application.Auth.Logout;
using JapaneseLearning.User.Application.Auth.Refresh;
using JapaneseLearning.User.Application.Auth.Register;
using JapaneseLearning.User.Application.Common.Exceptions;
using JapaneseLearning.User.Domain.Entities;
using JapaneseLearning.User.Domain.Users;
using Microsoft.Extensions.Logging;
using Moq;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using UserEntity = JapaneseLearning.User.Domain.Entities.User;

namespace JapaneseLearning.User.UnitTests.Logging;

public sealed class AuthenticationLoggingTests
{
    [Fact]
    public async Task SuccessEventsContainUserIdAndPreserveAuthenticationResults()
    {
        using var setup = new Setup();
        var user = setup.User;
        var register = new RegisterCommandHandler(setup.Users.Object, setup.Passwords.Object,
            setup.Loggers.CreateLogger<RegisterCommandHandler>());
        var registered = await register.Handle(new RegisterCommand(user.Username, user.Email, "password-sentinel"), default);
        setup.Users.Verify(repository => repository.AddAsync(
            It.Is<UserEntity>(value => value.Id == registered.Id && value.PasswordHash == "hash-sentinel"), default), Times.Once);

        var login = setup.Login();
        var loggedIn = await login.Handle(new LoginCommand(user.Email, "password-sentinel"), default);
        Assert.Equal("access-sentinel", loggedIn.AccessToken);
        Assert.Equal("refresh-sentinel", loggedIn.RefreshToken);

        var refresh = new RefreshTokenCommandHandler(setup.RefreshTokens.Object, setup.Users.Object,
            setup.Tokens.Object, setup.Loggers.CreateLogger<RefreshTokenCommandHandler>());
        var rotated = await refresh.Handle(new RefreshTokenCommand("refresh-sentinel"), default);
        Assert.Equal("access-sentinel", rotated.AccessToken);
        Assert.Equal("refresh-sentinel", rotated.RefreshToken);
        setup.RefreshTokens.Verify(repository => repository.AddAsync(
            It.Is<RefreshToken>(value => value.TokenHash == "token-hash-sentinel"), default), Times.Exactly(2));

        var logout = new LogoutCommandHandler(setup.RefreshTokens.Object, setup.Tokens.Object,
            setup.Loggers.CreateLogger<LogoutCommandHandler>());
        await logout.Handle(new LogoutCommand("refresh-sentinel"), default);
        setup.RefreshTokens.Verify(repository => repository.RevokeAsync(setup.RefreshToken.Id,
            It.IsAny<DateTime>(), default), Times.Exactly(2));

        Assert.Equal(4, setup.Events.Count);
        Assert.All(setup.Events, entry =>
        {
            Assert.Equal(LogEventLevel.Information, entry.Level);
            Assert.True(entry.Properties.ContainsKey("UserId"));
            Assert.True(entry.Properties.ContainsKey("EventId"));
        });
        setup.AssertNoSecrets();
    }

    [Theory]
    [InlineData(false, true, true, "INVALID_CREDENTIALS")]
    [InlineData(true, false, true, "USER_INACTIVE")]
    [InlineData(true, true, false, "INVALID_CREDENTIALS")]
    public async Task LoginRejectionsLogReasonsWithoutCredentials(
        bool exists, bool active, bool passwordValid, string expectedCode)
    {
        using var setup = new Setup(active);
        if (!exists)
            setup.Users.Setup(repository => repository.GetByEmailAsync(It.IsAny<string>(), default))
                .ReturnsAsync((UserEntity?)null);
        setup.Passwords.Setup(hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(passwordValid);
        var exception = await Assert.ThrowsAsync<UnauthorizedException>(() =>
            setup.Login().Handle(new LoginCommand(setup.User.Email, "password-sentinel"), default));
        Assert.Equal(expectedCode, exception.Code);
        var entry = Assert.Single(setup.Events);
        Assert.Equal(LogEventLevel.Warning, entry.Level);
        Assert.Equal(expectedCode, ((ScalarValue)entry.Properties["Reason"]).Value);
        setup.Tokens.Verify(service => service.CreateTokens(It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<UserRole>()), Times.Never);
        setup.AssertNoSecrets();
    }

    [Theory]
    [InlineData("invalid", "INVALID_REFRESH_TOKEN")]
    [InlineData("revoked", "REFRESH_TOKEN_REVOKED")]
    [InlineData("expired", "REFRESH_TOKEN_EXPIRED")]
    public async Task RefreshAndLogoutRejectBadTokensWithoutLoggingThem(string state, string expectedCode)
    {
        using var setup = new Setup();
        var token = state == "invalid" ? null : new RefreshToken(Guid.NewGuid(), setup.User.Id,
            "token-hash-sentinel", DateTime.UtcNow.AddDays(state == "expired" ? -1 : 1), DateTime.UtcNow);
        if (state == "revoked")
            typeof(RefreshToken).GetProperty(nameof(RefreshToken.RevokedAt))!.SetValue(token, DateTime.UtcNow);
        setup.RefreshTokens.Setup(repository => repository.GetByTokenHashAsync(It.IsAny<string>(), default))
            .ReturnsAsync(token);
        var refresh = new RefreshTokenCommandHandler(setup.RefreshTokens.Object, setup.Users.Object,
            setup.Tokens.Object, setup.Loggers.CreateLogger<RefreshTokenCommandHandler>());
        var logout = new LogoutCommandHandler(setup.RefreshTokens.Object, setup.Tokens.Object,
            setup.Loggers.CreateLogger<LogoutCommandHandler>());
        var first = await Assert.ThrowsAsync<UnauthorizedException>(() =>
            refresh.Handle(new RefreshTokenCommand("refresh-sentinel"), default));
        var second = await Assert.ThrowsAsync<UnauthorizedException>(() =>
            logout.Handle(new LogoutCommand("refresh-sentinel"), default));
        Assert.Equal(expectedCode, first.Code);
        Assert.Equal(expectedCode, second.Code);
        Assert.Equal(2, setup.Events.Count);
        Assert.All(setup.Events, entry =>
        {
            Assert.Equal(LogEventLevel.Warning, entry.Level);
            Assert.Equal(expectedCode, ((ScalarValue)entry.Properties["Reason"]).Value);
        });
        setup.RefreshTokens.Verify(repository => repository.RevokeAsync(It.IsAny<Guid>(),
            It.IsAny<DateTime>(), default), Times.Never);
        setup.AssertNoSecrets();
    }

    private sealed class Setup : IDisposable
    {
        private readonly Logger _logger;
        public ILoggerFactory Loggers { get; }
        private readonly CaptureSink _sink = new();
        public List<LogEvent> Events => _sink.Events;
        public Mock<IUserRepository> Users { get; } = new();
        public Mock<IRefreshTokenRepository> RefreshTokens { get; } = new();
        public Mock<IPasswordHasher> Passwords { get; } = new();
        public Mock<ITokenService> Tokens { get; } = new();
        public UserEntity User { get; }
        public RefreshToken RefreshToken { get; }

        public Setup(bool active = true)
        {
            _logger = new LoggerConfiguration().WriteTo.Sink(_sink).CreateLogger();
            Loggers = LoggerFactory.Create(builder => builder.AddSerilog(_logger));
            User = new UserEntity(Guid.NewGuid(), "username-sentinel", "email-sentinel",
                "hash-sentinel", UserRole.User, active, DateTime.UtcNow);
            RefreshToken = new RefreshToken(Guid.NewGuid(), User.Id, "token-hash-sentinel",
                DateTime.UtcNow.AddDays(1), DateTime.UtcNow);
            Users.Setup(repository => repository.GetByEmailAsync(It.IsAny<string>(), default)).ReturnsAsync(User);
            Users.Setup(repository => repository.GetByIdAsync(User.Id, default)).ReturnsAsync(User);
            Passwords.Setup(hasher => hasher.Hash(It.IsAny<string>())).Returns("hash-sentinel");
            Passwords.Setup(hasher => hasher.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            Tokens.Setup(service => service.CreateTokens(User.Id, User.Username, User.Email, User.Role))
                .Returns(new TokenResult("access-sentinel", "refresh-sentinel", DateTime.UtcNow.AddDays(1), 900));
            Tokens.Setup(service => service.HashRefreshToken(It.IsAny<string>())).Returns("token-hash-sentinel");
            RefreshTokens.Setup(repository => repository.GetByTokenHashAsync(It.IsAny<string>(), default))
                .ReturnsAsync(RefreshToken);
        }

        public LoginCommandHandler Login() => new(Users.Object, Passwords.Object, RefreshTokens.Object,
            Tokens.Object, Loggers.CreateLogger<LoginCommandHandler>());



        public void AssertNoSecrets()
        {
            using var output = new StringWriter();
            foreach (var entry in Events) new SafeJsonFormatter().Format(entry, output);
            Assert.DoesNotContain("-sentinel", output.ToString());
        }

        public void Dispose()
        {
            Loggers.Dispose();
            _logger.Dispose();
        }
    }
    private sealed class CaptureSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }}