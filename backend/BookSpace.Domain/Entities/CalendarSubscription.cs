using BookSpace.Domain.Common;

namespace BookSpace.Domain.Entities;

// One row per issued token - regenerating revokes the previous active row and inserts a new one, rather
// than mutating TokenHash in place, the same "reissue" convention Invitation already uses (see its own
// doc comment). At most one row per user may be ACTIVE (RevokedAtUtc IS NULL) at a time - enforced by a
// filtered unique index on UserId, the identical shape to Invitation's own filtered unique index. Only
// TokenHash (SHA-256, the same SecureTokenGenerator convention as RefreshToken/Invitation) is ever
// persisted for the token itself - the raw value is returned to the owner exactly once, at
// creation/regeneration time, and is never stored or logged anywhere afterward. No RowVersion column:
// unlike RefreshToken/Invitation (which guard a single-use CONSUME race that would otherwise silently
// lose an update), the only concurrent race here - two simultaneous regenerations - is already caught by
// the filtered unique index itself (the loser's INSERT of a second active row fails as an ordinary
// unique-constraint conflict via SaveChangesHandlingConflictsAsync), so a second concurrency mechanism
// would be redundant.
public sealed class CalendarSubscription : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
}
