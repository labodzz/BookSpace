namespace BookSpace.Application.Security;

// Closes a gap no earlier part of this codebase needed to close: AuthenticationService's IsUsableAsync
// (see its own doc comment) only re-checks live user/tenant state at Login/Refresh/AcceptInvitation time -
// never on an ordinary API call, since ICurrentUserContext reads the access token's claims directly, with
// no database round trip. That is fine for Suspended (see SuspendTenantCommandHandler's own doc comment: a
// bounded, documented, accepted access-token window) but not for a soft-deleted tenant - this task requires
// an old session to lose tenant API access the moment deletion completes, even though its JWT is, strictly,
// still unexpired. Checked once per authenticated request (see the Api-layer middleware that calls this),
// so deliberately as cheap as possible: two single-row lookups by primary key, no joins.
//
// Deliberately narrower than "the caller's account is in any non-Active status" - Suspended tenants and
// ordinarily-Inactive users keep their existing, already-documented bounded-access-token-window behavior
// unchanged; only the two states a tenant deletion itself writes (see DeleteTenantCommandHandler) are
// treated as an immediate hard cutoff: UserStatus.Withdrawn, or the caller's own tenant being
// TenantStatus.Deleted.
public interface IDeletedTenantAccessGuard
{
    // True when the request must be rejected because the caller's account was withdrawn, or their tenant
    // was soft-deleted, since their access token was issued. False for an unknown userId only in the sense
    // that it never throws - callers that reach this with a userId from a validated JWT's own `sub` claim
    // always expect a real row to exist; this treats a vanished row the same as "revoked" (fail closed),
    // never as "let it through".
    Task<bool> IsRevokedAsync(Guid userId, CancellationToken cancellationToken);
}
