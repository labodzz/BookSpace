using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using BookSpace.Application.Security;

namespace BookSpace.Api.Security;

internal sealed class CurrentUserContext(IHttpContextAccessor httpContextAccessor) : ICurrentUserContext
{
    public Guid? UserId => TryGetGuidClaim(JwtRegisteredClaimNames.Sub);

    public Guid? TenantId => TryGetGuidClaim("tenant_id");

    public IReadOnlyCollection<string> Roles =>
        httpContextAccessor.HttpContext?.User.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToArray()
            ?? [];

    private Guid? TryGetGuidClaim(string claimType)
    {
        var value = httpContextAccessor.HttpContext?.User.FindFirst(claimType)?.Value;
        return Guid.TryParse(value, out var parsed) ? parsed : null;
    }
}
