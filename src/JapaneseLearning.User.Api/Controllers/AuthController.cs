using JapaneseLearning.User.Application.Auth.Register;
using JapaneseLearning.User.Application.Auth.Login;
using JapaneseLearning.User.Application.Auth.Refresh;
using JapaneseLearning.User.Application.Auth.Logout;
using JapaneseLearning.User.Application.Abstractions.Security;
using Microsoft.AspNetCore.Authorization;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using JapaneseLearning.User.Api.Common.Responses;

namespace JapaneseLearning.User.Api.Controllers;

[ApiController]
[Route("api/auth")]
[ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status500InternalServerError)]
public sealed class AuthController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AuthController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    [HttpPost("register")]
    [ProducesResponseType(
        typeof(ApiResponse<RegisterResponse>),
        StatusCodes.Status201Created)]
    [ProducesResponseType(
        typeof(ApiResponse<object>),
        StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register(
        [FromBody] RegisterCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            command,
            cancellationToken);

        return StatusCode(
            StatusCodes.Status201Created,
            ApiResponse<RegisterResponse>.Ok(result, HttpContext));
    }

    [HttpPost("login")]
    [ProducesResponseType(
        typeof(ApiResponse<LoginResponse>),
        StatusCodes.Status200OK)]
    [ProducesResponseType(
        typeof(ApiResponse<object>),
        StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(
        typeof(ApiResponse<object>),
        StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Login(
        [FromBody] LoginCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            command,
            cancellationToken);

        return Ok(ApiResponse<LoginResponse>.Ok(result, HttpContext));
    }

    [HttpPost("refresh")]
    [ProducesResponseType(
    typeof(ApiResponse<RefreshTokenResponse>),
    StatusCodes.Status200OK)]
    [ProducesResponseType(
    typeof(ApiResponse<object>),
    StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh(
        [FromBody] RefreshTokenCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            command,
            cancellationToken);

        return Ok(ApiResponse<RefreshTokenResponse>.Ok(result, HttpContext));
    }

    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Logout(
        [FromBody] LogoutCommand command,
        CancellationToken cancellationToken)
    {
        await _sender.Send(
            command,
            cancellationToken);

        return NoContent();
    }

    [Authorize]
    [HttpGet("me")]
    [ProducesResponseType(typeof(ApiResponse<CurrentUserResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public IActionResult Me()
    {
        var result = new CurrentUserResponse(_currentUser.UserId, _currentUser.Username,
            _currentUser.Email, _currentUser.Role);
        return Ok(ApiResponse<CurrentUserResponse>.Ok(result, HttpContext));
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("admin-test")]
    [ProducesResponseType(typeof(ApiResponse<AdminTestResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public IActionResult AdminTest()
    {
        var result = new AdminTestResponse("You are an admin.", _currentUser.UserId, _currentUser.Role);
        return Ok(ApiResponse<AdminTestResponse>.Ok(result, HttpContext));
    }
}
