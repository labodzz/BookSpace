using BookSpace.Api.Logging;
using BookSpace.Application.Auth;
using BookSpace.Application.Logging;
using BookSpace.Application.Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BookSpace.Api.Controllers;

[ApiController]
[Route("auth")]
[AllowAnonymous]
public sealed class AuthController(
    IMediator mediator, IProblemDetailsService problemDetailsService, ICorrelationIdContext correlationIdContext) : ControllerBase
{
    [HttpPost("login")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new LoginCommandRequest(request.Email, request.Password), cancellationToken);
        if (!result.Succeeded)
        {
            return await UnauthorizedProblemAsync("Invalid email or password.", result.ErrorCode);
        }

        return Ok(ToResponse(result.Tokens!));
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(RefreshRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new RefreshCommandRequest(request.RefreshToken), cancellationToken);
        if (!result.Succeeded)
        {
            var message = result.ReuseDetected
                ? "Refresh token reuse detected; every session for this account has been revoked. Please log in again."
                : "Invalid or expired refresh token.";
            return await UnauthorizedProblemAsync(message, result.ErrorCode);
        }

        return Ok(ToResponse(result.Tokens!));
    }

    // Always 204, regardless of whether the presented token was found/already invalid - same
    // "don't leak state" reasoning as login/refresh's indistinguishable failure messages. The client
    // clears its own local tokens unconditionally either way, so there is nothing useful to report back.
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(LogoutRequest request, CancellationToken cancellationToken)
    {
        await mediator.Send(new LogoutCommandRequest(request.RefreshToken), cancellationToken);
        return NoContent();
    }

    // Login/refresh failures are ordinary (Succeeded == false) results, not exceptions -
    // LoginCommandRequestHandler/RefreshCommandRequestHandler never throw for "wrong password"/"bad
    // token", so they never reach the NotFoundException/ConflictException-driven IExceptionHandler
    // pipeline (see BookSpace.Api/ErrorHandling). This builds the same ProblemDetails+correlationId+
    // errorCode shape those handlers produce, by hand, so a 401 from /auth looks like every other error
    // the API returns. The message itself deliberately never reveals whether an account exists (same
    // wording for "no such user" and "wrong password"), and 401 - not 403 - is correct here: the caller
    // has presented no valid credential at all, there's no identity to be forbidden as.
    private async Task<IActionResult> UnauthorizedProblemAsync(string detail, string? errorCode)
    {
        HttpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Authentication failed.",
            Detail = detail,
        };

        if (errorCode is not null)
        {
            problemDetails.Extensions["errorCode"] = errorCode;
        }

        if (correlationIdContext.CorrelationId is { } correlationId)
        {
            problemDetails.Extensions["correlationId"] = correlationId;
            HttpContext.Response.Headers[CorrelationIdMiddleware.HeaderName] = correlationId;
        }

        var written = await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = HttpContext,
            ProblemDetails = problemDetails,
        });

        return written ? new EmptyResult() : StatusCode(StatusCodes.Status401Unauthorized);
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

public sealed record LogoutRequest(string RefreshToken);
