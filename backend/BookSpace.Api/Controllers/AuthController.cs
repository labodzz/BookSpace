using BookSpace.Application.Auth;
using BookSpace.Application.Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BookSpace.Api.Controllers;

[ApiController]
[Route("auth")]
[AllowAnonymous]
public sealed class AuthController(IMediator mediator) : ControllerBase
{
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new LoginCommand(request.Email, request.Password), cancellationToken);
        if (!result.Succeeded)
        {
            return Unauthorized(new { message = "Invalid email or password." });
        }

        return Ok(ToResponse(result.Tokens!));
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(RefreshRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new RefreshCommand(request.RefreshToken), cancellationToken);
        if (!result.Succeeded)
        {
            var message = result.ReuseDetected
                ? "Refresh token reuse detected; every session for this account has been revoked. Please log in again."
                : "Invalid or expired refresh token.";
            return Unauthorized(new { message });
        }

        return Ok(ToResponse(result.Tokens!));
    }

    private static object ToResponse(AuthTokens tokens) => new
    {
        accessToken = tokens.AccessToken,
        accessTokenExpiresAtUtc = tokens.AccessTokenExpiresAtUtc,
        refreshToken = tokens.RefreshToken,
        refreshTokenExpiresAtUtc = tokens.RefreshTokenExpiresAtUtc,
    };
}

public sealed record LoginRequest(string Email, string Password);

public sealed record RefreshRequest(string RefreshToken);
