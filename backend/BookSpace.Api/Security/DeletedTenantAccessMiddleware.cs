using System.IdentityModel.Tokens.Jwt;
using BookSpace.Application.Security;
using Microsoft.AspNetCore.Authorization;

namespace BookSpace.Api.Security;

// Placed after UseAuthentication/UseAuthorization (so HttpContext.User's claims are populated and routing
// has already matched an endpoint) and before MapControllers - see Program.cs. See
// IDeletedTenantAccessGuard's own doc comment for exactly what this closes and why it's narrower than
// "any non-Active account/tenant status".
//
// Skips the check entirely for an [AllowAnonymous] endpoint, even one a caller happened to attach a
// (possibly revoked) Bearer token to: Login/Refresh/Logout never require one, and the calendar feed
// endpoint authenticates via its own query-string token, never a JWT - neither should ever be rejected
// because of an unrelated, incidental Authorization header.
public sealed class DeletedTenantAccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IDeletedTenantAccessGuard guard)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            await next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated == true
            && Guid.TryParse(context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var userId)
            && await guard.IsRevokedAsync(userId, context.RequestAborted))
        {
            // No body, no WWW-Authenticate challenge of our own - the same bare 401 shape the JWT bearer
            // middleware itself already produces for an expired/invalid token, which the frontend's
            // existing authInterceptor already knows how to react to (attempt one refresh, which
            // AuthenticationService.RefreshAsync will also reject, then redirect to /login) - see
            // frontend/src/app/core/http/auth.interceptor.ts. No new frontend handling is needed for this.
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context);
    }
}
