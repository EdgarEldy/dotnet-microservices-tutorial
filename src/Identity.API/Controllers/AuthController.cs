using Identity.API.Dtos;
using Identity.API.Security;
using Identity.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Identity.API.Controllers;

/// <summary>
/// /api/v1/Auth/*: translates HTTP to service calls, nothing else. Input is validated by the
/// global ValidationFilter; errors are thrown by the services and become ProblemDetails.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
[Produces("application/json")]
public sealed class AuthController(IUserService userService, IAuthService authService) : ControllerBase
{
    /// <summary>
    /// Creates an unconfirmed account and publishes UserRegisteredEvent. 202 Accepted, with no
    /// body: the account is not usable until confirmed, and the answer is the same when the
    /// e-mail is already registered.
    /// </summary>
    [HttpPost("[action]")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        await userService.RegisterAsync(request, cancellationToken);
        return Accepted();
    }

    [HttpGet("[action]")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ConfirmEmail([FromQuery] ConfirmEmailRequest request, CancellationToken cancellationToken)
    {
        await userService.ConfirmEmailAsync(request, cancellationToken);
        return NoContent();
    }

    [HttpPost("[action]")]
    [ProducesResponseType(typeof(TokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<TokenResponse>> Login(LoginRequest request, CancellationToken cancellationToken) =>
        await authService.LoginAsync(request, cancellationToken);

    [HttpPost("[action]")]
    [ProducesResponseType(typeof(TokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<TokenResponse>> Refresh(RefreshRequest request, CancellationToken cancellationToken) =>
        await authService.RefreshAsync(request, cancellationToken);

    [Authorize]
    [HttpPost("[action]")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        await authService.LogoutAsync(User.GetCurrentSession(), cancellationToken);
        return NoContent();
    }

    [Authorize]
    [HttpGet("[action]")]
    [ProducesResponseType(typeof(UserProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserProfileResponse>> Me(CancellationToken cancellationToken) =>
        await userService.GetProfileAsync(User.GetUserId(), cancellationToken);

    /// <summary>
    /// 202 Accepted whether the e-mail exists or not; PasswordResetRequestedEvent is only
    /// published when it does.
    /// </summary>
    [HttpPost("[action]")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        await userService.ForgotPasswordAsync(request, cancellationToken);
        return Accepted();
    }

    [HttpPost("[action]")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        await userService.ResetPasswordAsync(request, cancellationToken);
        return NoContent();
    }
}
