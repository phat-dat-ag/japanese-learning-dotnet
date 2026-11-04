using System.Text.Json;
using JapaneseLearning.User.Api.Controllers;
using JapaneseLearning.User.Api.Common.Logging;
using JapaneseLearning.User.Api.Common.Responses;
using JapaneseLearning.User.Application.Abstractions.Security;
using JapaneseLearning.User.Application.Auth.Login;
using JapaneseLearning.User.Application.Auth.Logout;
using JapaneseLearning.User.Application.Auth.Refresh;
using JapaneseLearning.User.Application.Auth.Register;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Moq;

namespace JapaneseLearning.User.UnitTests.Logging;

public sealed class AuthSuccessContractTests
{
    [Fact]
    public async Task AuthSuccessesWrapTheirDataAndShareRequestMetadata()
    {
        var sender = new Mock<ISender>();
        var registered = new RegisterResponse(Guid.NewGuid(), "learner", "learner@example.test", DateTime.UtcNow);
        var login = new LoginResponse("test-access", "test-refresh", 900);
        var refreshed = new RefreshTokenResponse("new-access", "new-refresh", 900);
        sender.Setup(value => value.Send(It.IsAny<RegisterCommand>(), It.IsAny<CancellationToken>())).ReturnsAsync(registered);
        sender.Setup(value => value.Send(It.IsAny<LoginCommand>(), It.IsAny<CancellationToken>())).ReturnsAsync(login);
        sender.Setup(value => value.Send(It.IsAny<RefreshTokenCommand>(), It.IsAny<CancellationToken>())).ReturnsAsync(refreshed);
        var currentUser = new Mock<ICurrentUser>();
        currentUser.SetupGet(value => value.UserId).Returns(registered.Id);
        currentUser.SetupGet(value => value.Username).Returns(registered.Username);
        currentUser.SetupGet(value => value.Email).Returns(registered.Email);
        currentUser.SetupGet(value => value.Role).Returns("Admin");
        var context = new DefaultHttpContext { TraceIdentifier = "success-contract" };
        context.Items[CorrelationIdMiddleware.TraceIdKey] = "4bf92f3577b34da6a3ce929d0e0e4736";
        var controller = new AuthController(sender.Object, currentUser.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var registration = Assert.IsType<ObjectResult>(await controller.Register(
            new RegisterCommand("learner", "learner@example.test", "test-password"), default));
        Assert.Equal(201, registration.StatusCode);
        Assert.Same(registered, Assert.IsType<ApiResponse<RegisterResponse>>(registration.Value).Data);
        var loggedIn = Assert.IsType<OkObjectResult>(await controller.Login(new LoginCommand("learner@example.test", "test-password"), default));
        Assert.Same(login, Assert.IsType<ApiResponse<LoginResponse>>(loggedIn.Value).Data);
        var rotated = Assert.IsType<OkObjectResult>(await controller.Refresh(new RefreshTokenCommand("test-refresh"), default));
        Assert.Same(refreshed, Assert.IsType<ApiResponse<RefreshTokenResponse>>(rotated.Value).Data);
        var me = Assert.IsType<OkObjectResult>(controller.Me());
        var identity = Assert.IsType<ApiResponse<CurrentUserResponse>>(me.Value);
        Assert.Equal(registered.Id, identity.Data!.UserId);
        Assert.Equal(registered.Email, identity.Data.Email);
        var admin = Assert.IsType<OkObjectResult>(controller.AdminTest());
        Assert.Equal("You are an admin.", Assert.IsType<ApiResponse<AdminTestResponse>>(admin.Value).Data!.Message);
        foreach (var value in new[] { registration.Value, loggedIn.Value, rotated.Value, me.Value, admin.Value })
        {
            var json = JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.Equal(JsonValueKind.Object, json.GetProperty("data").ValueKind);
            Assert.False(json.TryGetProperty("error", out _));
            var meta = json.GetProperty("meta");
            Assert.Equal("success-contract", meta.GetProperty("correlationId").GetString());
            Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", meta.GetProperty("traceId").GetString());
            Assert.Equal(TimeSpan.Zero, meta.GetProperty("timestamp").GetDateTimeOffset().Offset);
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
