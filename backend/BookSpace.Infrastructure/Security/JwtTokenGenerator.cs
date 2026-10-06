using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BookSpace.Infrastructure.Security;

internal sealed class JwtTokenGenerator(IOptions<AuthOptions> authOptions) : IJwtTokenGenerator
{
    public AccessToken GenerateAccessToken(User user, IReadOnlyCollection<string> roles)
    {
        var options = authOptions.Value;
        var expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(options.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
        };
        // Omitted entirely for the global SysAdmin (user.TenantId == null) - never a fake/empty value.
        // CurrentUserContext.TenantId already tolerates a missing claim (TryGetGuidClaim returns null
        // rather than throwing), so there is nothing for a reader to Guid.Parse against.
        if (user.TenantId is { } tenantId)
        {
            claims.Add(new Claim("tenant_id", tenantId.ToString()));
        }
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: claims,
            expires: expiresAtUtc.UtcDateTime,
            signingCredentials: signingCredentials);

        return new AccessToken(new JwtSecurityTokenHandler().WriteToken(token), expiresAtUtc);
    }
}
