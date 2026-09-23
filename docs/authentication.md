# Authentication and Refresh Token Security

## Invitation acceptance (`POST /auth/accept-invitation`)

A fourth pre-tenant-context flow alongside login/refresh/logout, added by the user-administration batch
(full lifecycle writeup in [user-administration.md](user-administration.md) §4) but documented here for
its security-model overlap with the rest of this file:

- The presented invitation token is a 512-bit random value (`SecureTokenGenerator`, the exact same
  generate-and-hash algorithm refresh tokens use - see "Token model" above). Only its SHA-256 hash is
  ever looked up or persisted; the raw value is returned to the inviting admin exactly once, at
  invitation-creation time, and never logged anywhere in this flow.
- Every reason a token doesn't work - unknown, already accepted, revoked, expired, or the underlying
  `User` somehow no longer `Invited` - collapses into the identical generic failure (`Invitation.Invalid`,
  `401`), the same "don't leak account state" philosophy login/refresh already apply to their own
  failures.
- Single-use is enforced by `Invitation.RowVersion` (a real SQL Server `rowversion` column, the same
  mechanism `RefreshToken.RowVersion` uses for rotation races - see "RowVersion concurrency mechanism"
  above): a losing concurrent acceptance of the same token gets `ConflictException` from
  `SaveChangesHandlingConflictsAsync`, mapped to the identical generic failure rather than a distinct
  error.
- Acceptance sets the password hash and flips `User.Status` to `Active` in the same `SaveChangesAsync`
  call that marks the invitation accepted - one atomic write, not two.
- Deliberately issues **no** access/refresh token - the newly-activated user logs in separately afterward
  via the ordinary `POST /auth/login`. Accepting an invitation is proof you set a password, not proof of
  an authenticated session.

## Token model

- **Access token**: a short-lived, stateless JWT (`AccessTokenMinutes`, currently 15). Carries the
  user's id, roles, and `tenant_id` claim. Never stored server-side; expiry is enforced purely by
  clients no longer being able to present a valid signature/`exp`.
- **Refresh token**: a long-lived (`RefreshTokenDays`, currently 14), high-entropy random value. Only
  its SHA-256 hash is stored (`RefreshToken.TokenHash`); the raw value is returned to the client once,
  at issuance, and never persisted. Used solely to obtain a new access+refresh token pair via
  `POST /auth/refresh` - there is no other use for it.

## Token family and rotation

Every refresh token belongs to a `FamilyId`, assigned once at login and carried unchanged through every
subsequent rotation in that lineage. Rotating a token (`AuthenticationService.RefreshAsync` ->
`IssueTokensAsync`) does three things in one write: inserts the new child token, sets the presented
token's `ReplacedByTokenId` to the child's id, and sets its `RevokedAtUtc`. A token is single-use by
convention - presenting the same one twice is exactly the signal reuse detection watches for.

## Refresh request precedence

`RefreshAsync` evaluates a presented token in this exact order:

1. **Not found** (unknown hash) -> plain failure, no reuse detection (nothing is known about this token).
2. **Reused / revoked / replaced** (`RevokedAtUtc` or `ReplacedByTokenId` already set) -> reuse
   detected, checked **before** expiry.
3. **Expired** (`ExpiresAtUtc <= now`, and not already caught by step 2) -> plain failure.
4. **Valid** -> rotate.

Step 2 is deliberately ordered before step 3: a token that was already rotated by its legitimate owner,
and has *also* since passed its own expiry window, must still trigger reuse detection. An attacker who
steals a token and simply waits out its expiry before replaying it must not get a quieter "just
invalid" outcome that skips revoking the family - that was a real bug, found and fixed this session.

## Actual reuse vs. a concurrent legitimate race

These are two different scenarios with two different, deliberately different, responses.

**Actual reuse**: a request presents a token whose `RevokedAtUtc`/`ReplacedByTokenId` was *already set*
at the moment this request read it - i.e., some earlier, separate request already consumed it. This is
either a stale client retry using a token it should have discarded, or a stolen token being replayed
after the legitimate rotation already happened. Response: revoke the entire family
(`RevokeFamilyAsync`), report `ReuseDetected = true`. Every token descended from that login is now
invalid; the legitimate user must log in again.

**Concurrent race**: two requests both read the *same, still-valid* token at effectively the same
instant, before either has committed its rotation. Both pass the precedence checks above (neither sees
the other's in-flight write). One wins the database-level race (see
[optimistic-concurrency.md](optimistic-concurrency.md)); the other's `SaveChangesAsync` throws a
`ConflictException` from a `RowVersion` mismatch. Response: the loser fails cleanly
(`RefreshResult(false, null, false)`) - **the family is NOT revoked**, and the winner's newly-issued
token remains fully valid and usable.

These are intentionally different because they mean different things. Reuse detection is keyed on what
a request *read*: presenting a token already known-consumed at read time is a genuine "this token
escaped its single-use contract" signal. A concurrent loser never read a consumed token - it read a
valid one and lost a race that resolved entirely after that read, most plausibly caused by an innocent
client-side double-fire (a retry, two tabs) rather than theft. Revoking the family on every concurrency
conflict would mean the *legitimate winner* gets logged out because their own losing duplicate request
raced them - a disproportionate response to what is likely a client bug, not an attack. See the code
comment on `AuthenticationService.RefreshAsync`'s `ConflictException` catch block for the same
reasoning in context, and `RefreshTokenRotationConcurrencyTests` for the test that proves the winner's
token survives untouched.

## RowVersion concurrency mechanism

`RefreshToken.RowVersion` is a SQL Server `rowversion` column (server-generated, `IsRowVersion()`).
`SaveChangesHandlingConflictsAsync` (shared with the unique-constraint-conflict path, see
[optimistic-concurrency.md](optimistic-concurrency.md)) catches the resulting `DbUpdateConcurrencyException`
and translates it to `ConflictException`. Because EF Core wraps the insert (new child token) and update
(revoking the presented token) in one transaction, a losing request's entire attempt - including its
half-built child token - rolls back atomically. No orphan token is ever left behind.

## Logout

`POST /auth/logout` (`LogoutCommandRequest` -> `AuthenticationService.LogoutAsync`) takes the caller's
raw refresh token, hashes it, looks it up, and - if found, active or already rotated - calls the same
`RevokeFamilyAsync` reuse detection already uses, revoking every still-active token descended from that
one login. A voluntary logout and a detected theft both end the same way: every token in that lineage
stops working.

This revokes only the *calling session's* family - "log out this device," not "log out every device the
user is signed into." Always returns 204, even for an unknown/already-invalid token: logout never
reveals that distinction (same "don't leak state" reasoning as login/refresh's identical failure
messages), and the client clears its own local tokens unconditionally regardless of the response.

Revocation deliberately does not require the presented token to still be the family's current one: this
endpoint only ever runs from an explicit user action (a real "Log out" click) - the frontend's automatic
reaction to a failed refresh calls `AuthService.clearExpiredSession()` instead (local-only, no server
call; see "Cross-tab refresh coordination" below), never this endpoint. That means a tab that's fallen
behind - still holding a pre-rotation token because it missed a `BroadcastChannel` update, or was asleep
through a sibling tab's refresh - can still authoritatively end "this device's" session via logout, which
is exactly what a deliberate Logout click should do. An earlier version of this endpoint required the
presented token to still be current, to guard against the *automatic* interceptor-triggered logout the
old design had; that guard became unnecessary (and actively wrong for the sleepy-tab case) once the
automatic path was moved to `clearExpiredSession()` instead.

The frontend (`AuthService.logout()`) sends this best-effort - local state is cleared first and always,
whether or not the request reaches the server or succeeds. It also emits an internal `loggedOut$` signal
that cancels any `refreshAccessToken()` call still in flight at that moment (`takeUntil`) - without this,
a refresh that started just before logout could complete afterward and silently write fresh tokens back
into storage, undoing the logout the user just performed.

`logout()` is reserved for one thing: the user actually asked to end the session (a "Log out" click).
Every other place a session ends locally - most notably the interceptor's reaction to a refresh that
comes back 401 - calls `AuthService.clearExpiredSession()` instead, which tears down local state exactly
like `logout()` but never calls the server. A token the backend just rejected has nothing left to revoke,
and (before the fix below) calling the server here at all was how an innocent client-side race turned into
a cross-tab outage - `clearExpiredSession()` removes the temptation entirely, rather than relying on the
backend catching it.

## Cross-tab refresh coordination

Browser tabs of the same origin share one `localStorage`, but each tab has its OWN `sessionStorage` -
before this fix, each tab also ran its own independent copy of `AuthService` with no awareness of the
others. Two tabs idle past the access
token's 15-minute lifetime and then both making a request at once would both hit 401 and both call
`refreshAccessToken()` - a real race against the *same* refresh token, with only one winner at the
backend's `RowVersion` check (see "Actual reuse vs. a concurrent legitimate race" above). The loser's
failed refresh used to be treated as "this session is over," which - combined with the interceptor
calling the server-side `logout()` on any refresh failure - is exactly how one tab losing an innocent race
could revoke the *other* tab's brand-new, actively-used session. Three mechanisms now prevent this at the
client, with `RowVersion` kept as the last-resort backend safety net rather than the primary defense:

1. **Single-tab dedup** (`refreshInFlight$`): several requests failing 401 at once in the *same* tab share
   one in-flight refresh, unchanged from before.
2. **Cross-tab lock** (`navigator.locks.request('bookspace-auth-refresh', ...)`): only one tab of the
   browser may have a refresh actually in flight against the backend at a time; others queue for the same
   lock instead of racing it. Browsers without the Web Locks API fall back to running the refresh directly
   - no cross-tab dedup, but still correct, still backed by `RowVersion`.
3. **Storage re-sync on lock acquisition**: the moment a queued tab's turn comes up, it re-reads storage
   before doing anything else. If a sibling tab already rotated the token while this one was waiting, its
   result is sitting there already - this tab adopts it and skips the network call entirely, rather than
   firing a now-redundant request of its own.

A `BroadcastChannel('bookspace-auth')` closes the remaining gap: the tab that actually performs a refresh
posts the new tokens to every other tab immediately (so they update their in-memory state without waiting
to be caught out by a stale access token first), and an explicit `logout()` posts a "logged out" message
so every other tab ends its session too, instead of only the tab the user actually clicked in. A receiving
tab persists a `tokens-updated` broadcast into its OWN active storage, not just its in-memory signal -
`sessionStorage` isn't shared, so without this a session-only tab would keep a stale copy that the
storage re-sync check above could later mistake for "someone else's newer" value and wrongly revert to.

Without `navigator.locks` (or in the narrow window before a `BroadcastChannel` message arrives), a losing
tab's own refresh can still be rejected by the backend at the same moment a sibling tab's concurrent
refresh wins. `clearExpiredSession(rejectedRefreshToken)` therefore takes the *specific* token that
attempt used - not "whatever the signal currently holds" - and no-ops entirely (leaving both memory and
storage untouched) if that token is no longer the current one. This distinction matters because a
sibling tab's broadcast can arrive and update this tab's in-memory signal WHILE its own now-doomed request
is still in flight, using the old token; reading "current" state at the moment the rejection resolves would
see the sibling's newer session and wrongly tear it down - reading the token that was actually attempted,
captured once before the request was even sent, is immune to that. `logout()` has no equivalent check: it
always clears unconditionally, because an explicit logout (and a "logged out" broadcast received from
another tab) means the whole family was just revoked server-side - there is no "newer session" left to
protect at that point.

## Login rejects any non-Active user

`AuthenticationService.LoginAsync` checks `user.Status != UserStatus.Active`, short-circuited via `||`
**before** `IPasswordHasher.Verify` is even called - a deactivated (`Inactive`) or still-pending
(`Invited`, never yet given a real password) user presenting their genuinely-correct old/eventual
password still fails, without spending a PBKDF2 verification on a login that can never succeed anyway.
The failure is the exact same generic `Auth.InvalidCredentials` result an unknown email or a wrong
password produces - login never reveals account status to an outside prober. See
[user-administration.md](user-administration.md) §1 for the full `UserStatus` lifecycle this check
enforces.

## Bounded `/auth/refresh` call

`AuthService.refreshAccessToken()` (frontend) wraps the actual `POST /auth/refresh` HTTP call in
`timeout(REFRESH_TIMEOUT_MS)` (15 seconds). This matters specifically because that call runs inside
`navigator.locks.request('bookspace-auth-refresh', ...)` and is shared app-wide through a single
`refreshInFlight$` observable (see "Cross-tab refresh coordination" below) - a request that never settles
would otherwise hold the cross-tab lock forever, leaving every other component/tab that also needs a
token refresh waiting on that same never-resolving observable with no error to ever react to. A timeout
is treated identically to a network error or a 5xx: it says nothing about whether the backend actually
rejected the session, so it never triggers `clearExpiredSession()` - only a confirmed `401` does that.

## Deferred session-management decisions

Explicitly out of scope for this remediation pass, tracked in [open-questions.md](open-questions.md):
whether logout should also offer an "every session for this user" option (today it only revokes the
calling device), absolute refresh-token/session lifetime (today it slides indefinitely as long as the
token keeps getting refreshed), and whether a password change should revoke all outstanding
refresh-token families (no password-change feature exists yet at all). Do not assume a specific answer
to any of these without opening that question.
