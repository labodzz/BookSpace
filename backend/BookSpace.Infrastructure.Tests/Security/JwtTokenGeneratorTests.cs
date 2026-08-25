using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Security;

public sealed class JwtTokenGeneratorTests
{
    private static readonly AuthOptions AuthConfig = new()
    {
        Issuer = "bookspace-tests",
        Audience = "bookspace-tests",
        SigningKey = "unit-test-signing-key-needs-to-be-long-enough-1234567890",
        AccessTokenMinutes = 15,
        RefreshTokenDays = 14,
    };

    private static readonly User TestUser = new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        FirstName = "Test",
        LastName = "User",
        Email = "test.user@bookspace.test",
        PasswordHash = "irrelevant",
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private readonly JwtTokenGenerator _generator = new(Options.Create(AuthConfig));

    [Fact]
    public void GenerateAccessToken_IncludesExpectedClaims()
    {
        var roles = new[] { "TenantAdmin", "Member" };

        var accessToken = _generator.GenerateAccessToken(TestUser, roles);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(accessToken.Token);
        Assert.Equal(TestUser.Id.ToString(), jwt.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.Equal(TestUser.Email, jwt.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Email).Value);
        Assert.Equal(TestUser.TenantId.ToString(), jwt.Claims.Single(c => c.Type == "tenant_id").Value);
        Assert.Equal(
            roles.OrderBy(r => r),
            jwt.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).OrderBy(r => r));
    }

    [Fact]
    public void GenerateAccessToken_SetsExpiryAccordingToConfiguredMinutes()
    {
        var before = DateTimeOffset.UtcNow;

        var accessToken = _generator.GenerateAccessToken(TestUser, []);

        var expectedExpiry = before.AddMinutes(AuthConfig.AccessTokenMinutes);
        Assert.True(Math.Abs((accessToken.ExpiresAtUtc - expectedExpiry).TotalSeconds) < 5);
    }

    [Fact]
    public void GenerateAccessToken_ProducesTokenValidatableWithConfiguredIssuerAudienceAndKey()
    {
        var accessToken = _generator.GenerateAccessToken(TestUser, ["Member"]);

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = AuthConfig.Issuer,
            ValidateAudience = true,
            ValidAudience = AuthConfig.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(AuthConfig.SigningKey)),
            ValidateLifetime = true,
        };

        var principal = new JwtSecurityTokenHandler().ValidateToken(accessToken.Token, validationParameters, out _);

        Assert.NotNull(principal.Identity);
        Assert.True(principal.Identity!.IsAuthenticated);
    }
}
