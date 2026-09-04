# Authentication and Refresh Token Security

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

## Deferred session-management decisions

Explicitly out of scope for this remediation pass, tracked in [open-questions.md](open-questions.md):
absolute refresh-token/session lifetime (today it slides indefinitely as long as the token keeps
getting refreshed), what logout should revoke (one session vs. every session for the user), and
whether a password change should revoke all outstanding refresh-token families. None of these are
implemented today; do not assume a specific answer to any of them without opening that question.
