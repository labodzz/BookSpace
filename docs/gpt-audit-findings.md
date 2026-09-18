# GPT Repo Audit — Triage Log

Source: full-repo static audit run against a synthetic union of `wip/session-snapshot` (ddb28be) +
`feature/wp6-frontend-shell-and-auth` (c5a941b). Not executed against a running suite — claims verified
manually, one at a time, against the actual current code before any fix is applied.

Status legend: `TODO` not yet reviewed · `FIXED` verified real, fixed · `WONTFIX` reviewed, not worth
fixing (reason given) · `INVALID` GPT's claim doesn't hold up against the real code · `DISCUSS` needs a
product/design decision before fixing.

## Correctness bugs

- [ ] C1 — approver assignment doesn't require an approval-capable role — TODO
- [ ] C2 — approval-authorization failure returns 409 instead of 403 — TODO
- [ ] C3 — reuse-detection message overstates scope ("every session") — TODO
- [ ] C4 — `OccurrenceCount <= 0` passes validation — TODO
- [ ] C5 — huge recurrence interval can overflow/throw — TODO
- [ ] C6 — monthly occurrence estimator rejects valid long series — TODO
- [ ] C7 — recurring-series "past" check uses UTC date, not resource-local instant — TODO
- [ ] C8 — `ToDate = DateOnly.MaxValue` crashes availability handler — TODO
- [ ] C9 — invalid numeric resource status accepted — TODO
- [ ] C10 — unmapped Windows timezone IDs silently fall back to viewer's local timezone — TODO
- [ ] C11 — one-off bookings silently normalize nonexistent DST local times — TODO
- [ ] C12 — frontend/backend date-range boundary mismatch (92 days) — TODO
- [ ] C13 — multi-day blackout displayed only on its start day — TODO

## Security risks

- [ ] S1 — `isApiRequest` prefix match can leak bearer token to a look-alike origin — TODO
- [ ] S2 — login timing can reveal whether an email exists — TODO
- [ ] S3 — `/auth/refresh` and `/auth/logout` have no rate limit — TODO
- [ ] S4 — no max length on email/password/refresh-token inputs — TODO
- [ ] S5 — cancellation reason sent as a URL query parameter — TODO
- [ ] S6 — failed-login log includes raw attacker-supplied email — TODO
- [ ] S7 — `AuthOptions` not validated at startup (issuer/audience/lifetimes) — TODO
- [ ] S8 — rate limiting keys on `RemoteIpAddress` with no forwarded-header config — TODO

## Consistency violations

- [ ] K1 — most handlers named `XxxCommandHandler`, not `XxxCommandRequestHandler` — TODO
- [ ] K2 — 4 commands reuse shared `Unit` response instead of a dedicated response — TODO
- [ ] K3 — 15 requests have no paired FluentValidation validator — TODO
- [ ] K4 — Auth/Users requests not grouped into CRUD-verb folders — TODO
- [ ] K5 — some domain conflicts have no machine-readable error code — TODO

## Concurrency / data integrity

- [ ] D1 — family revocation (reuse/logout) can lose a race against rotation — TODO
- [ ] D2 — cancellation races approve/reject without the resource lock — TODO
- [ ] D3 — blackout conflict reporting not concurrency-safe with booking creation — TODO
- [ ] D4 — `finalize()` placement lets `refreshInFlight$` reset before the shared refresh finishes — TODO
- [ ] D5 — several list screens let a stale HTTP response overwrite newer state — TODO
- [ ] D6 — rotated/expired refresh-token rows are never cleaned up — TODO

## Frontend-specific

- [x] F1 — `clearExpiredSession()` reads "current" token instead of the one that actually failed — **FIXED**.
      Confirmed real: a sibling tab's broadcast can update `this.tokens` while this tab's own doomed
      request is still in flight, so reading "current" at the moment of rejection sees the sibling's
      session and wrongly tears it down. Fix: capture the attempted token as a local value before the
      HTTP call, pass it explicitly, compare against it instead of re-reading `this.tokens()`.
- [x] F2 — receiving a `tokens-updated` broadcast doesn't persist to the receiving tab's own storage — **FIXED**.
      Confirmed real and directly enables F1's failure mode for session-only logins: `sessionStorage` is
      per-tab, not shared, so a broadcast that only updated the in-memory signal left a stale value in
      this tab's own storage. Fix: `saveToActiveStorage()` on receipt, same as any other rotation.
- [ ] F3 — `isAuthenticated` doesn't re-evaluate purely from time passing — TODO
- [ ] F4 — calendar/dashboard silently truncates after 500 bookings — TODO
- [ ] F5 — `isApprover`-style role checks computed once, not reactively — TODO
- [ ] F6 — dashboard sets loading=false before parallel requests resolve — TODO
- [ ] F7 — some HTTP subscriptions have no local error handling — TODO

## Test coverage gaps

- [ ] T1 — no DB-level test for reuse/logout revocation racing rotation — TODO
- [ ] T2 — no concurrency test for cancel racing approve/reject — TODO
- [ ] T3 — auth frontend tests miss the cross-tab failure orderings — TODO (partially addressed by F1/F2 fix tests)
- [ ] T4 — many components/services have no spec file at all — TODO
- [ ] T5 — no CI workflow in the repo — TODO

## Dead code

- Nothing confirmed by the audit itself.

## Documentation drift

- [x] DOC1 — authentication.md wrongly implies sessionStorage is shared across tabs — **FIXED** (fixed as part of F1/F2, since the wrong assumption was the actual root cause of both)
- [ ] DOC2 — bookings-and-concurrency.md says approval workflow isn't implemented (stale) — TODO
- [ ] DOC3 — open-questions.md says TenantAdmin cancellation doesn't exist (stale) — TODO
- [ ] DOC4 — concurrency docs say cancellation needs no serialization (misses D2) — TODO
