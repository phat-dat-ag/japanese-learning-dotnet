using System.Text.Json;
using JapaneseLearning.User.Api.Controllers;
using JapaneseLearning.User.Application.Abstractions.Security;
using JapaneseLearning.User.Application.Auth.Login;
using JapaneseLearning.User.Application.Auth.Logout;
using JapaneseLearning.User.Application.Auth.Refresh;
using JapaneseLearning.User.Application.Auth.Register;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace JapaneseLearning.User.UnitTests.Logging;

public sealed class AuthSuccessContractTests
{
    [Fact]
    public async Task RegisterLoginAndRefreshKeepTheirDirectSuccessDtos()
    {
        var sender = new Mock<ISender>();
        var registered = new RegisterResponse(Guid.NewGuid(), "learner", "learner@example.test", DateTime.UtcNow);
        var login = new LoginResponse("test-access", "test-refresh", 900);
        var refreshed = new RefreshTokenResponse("new-access", "new-refresh", 900);
        sender.Setup(value => value.Send(It.IsAny<RegisterCommand>(), It.IsAny<CancellationToken>())).ReturnsAsync(registered);
        sender.Setup(value => value.Send(It.IsAny<LoginCommand>(), It.IsAny<CancellationToken>())).ReturnsAsync(login);
        sender.Setup(value => value.Send(It.IsAny<RefreshTokenCommand>(), It.IsAny<CancellationToken>())).ReturnsAsync(refreshed);
        var controller = new AuthController(sender.Object, Mock.Of<ICurrentUser>());

        var registration = Assert.IsType<ObjectResult>(await controller.Register(
            new RegisterCommand("learner", "learner@example.test", "test-password"), default));
        Assert.Equal(201, registration.StatusCode);
        Assert.Same(registered, registration.Value);
        var loggedIn = Assert.IsType<OkObjectResult>(await controller.Login(new LoginCommand("learner@example.test", "test-password"), default));
        Assert.Same(login, loggedIn.Value);
        var rotated = Assert.IsType<OkObjectResult>(await controller.Refresh(new RefreshTokenCommand("test-refresh"), default));
        Assert.Same(refreshed, rotated.Value);
        foreach (var value in new[] { registration.Value, loggedIn.Value, rotated.Value })
        {
            var json = JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.False(json.TryGetProperty("success", out _));
            Assert.False(json.TryGetProperty("data", out _));
            Assert.False(json.TryGetProperty("meta", out _));
        }
    }

    [Fact]
    public async Task LogoutKeepsItsEmpty204Response()
    {
        var sender = new Mock<ISender>();
        sender.Setup(value => value.Send(It.IsAny<IRequest>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var controller = new AuthController(sender.Object, Mock.Of<ICurrentUser>());
        Assert.Equal(204, Assert.IsType<NoContentResult>(await controller.Logout(new LogoutCommand("test-refresh"), default)).StatusCode);
    }
}
