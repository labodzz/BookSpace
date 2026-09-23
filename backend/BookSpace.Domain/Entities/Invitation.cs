using BookSpace.Domain.Common;

namespace BookSpace.Domain.Entities;

// One row per invitation attempt (not per user) - reissuing revokes the previous active row and
// inserts a new one, rather than mutating it in place, so the history of who was invited when, and by
// whom, is never overwritten. UserId points at the User row created (Status = Invited) at the same
// time as this invitation - see InviteUserCommandRequest. The raw token is never stored, only
// TokenHash (SHA-256, same convention as RefreshToken.TokenHash) - see AuthenticationService.
public sealed class Invitation : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }

    // SQL Server rowversion, EF-managed - lets AcceptInvitation detect "another request already
    // accepted/consumed this exact row since I read it", the same mechanism RefreshToken rotation uses
    // to make single-use enforcement race-proof rather than merely convention-based.
    public byte[] RowVersion { get; set; } = [];
}
