# Audit Remediation Summary

Summarizes the fixes implemented and verified in the `fix/auth-refresh-token-security`,
`fix/tenant-isolation-fail-closed`, `fix/resource-integrity-and-concurrency`, and
`fix/availability-correctness-and-performance` branches, and the items deliberately deferred out of
that pass. See each branch's commits for full detail; this is the index.

## Resolved

| Area | Finding | Branch |
|---|---|---|
| Auth | Refresh-token reuse detection was checked after expiry, letting a stolen-then-expired token skip family revocation | `fix/auth-refresh-token-security` |
| Auth | Concurrent refresh requests on the same token could both succeed, minting two live sibling tokens | `fix/auth-refresh-token-security` |
| Auth | A real JWT signing key was tracked in `appsettings.Development.json` since WP-2 | `fix/auth-refresh-token-security` |
| Tenancy | Global tenant query filter failed open (saw every tenant) on a missing tenant context | `fix/tenant-isolation-fail-closed` |
| Tenancy | Fail-closed filter broke login/refresh, which run with no tenant context yet | `fix/tenant-isolation-fail-closed` |
| Resources | `Archived` resources were reachable/rewritable through the general Update action | `fix/resource-integrity-and-concurrency` |
| Resources | `AvailabilityRule`/`BlackoutPeriod`/`ResourceApprover` could be created against an Archived parent | `fix/resource-integrity-and-concurrency` |
| Resources | `BlackoutPeriod` Update had no restriction on backdating `StartUtc` into the past | `fix/resource-integrity-and-concurrency` |
| Resources | `Resource.Capacity` could be reduced below existing active bookings' peak demand | `fix/resource-integrity-and-concurrency` |
| Resources | Capacity sweep-line miscounted back-to-back (touching, non-overlapping) bookings as overlapping | `fix/resource-integrity-and-concurrency` |
| Resources | `Resource`/`BlackoutPeriod` had no optimistic concurrency guard (last-write-wins) | `fix/resource-integrity-and-concurrency` |
| Availability | Query ignored `Resource.Status` - Maintenance/Inactive resources reported full bookable capacity | `fix/availability-correctness-and-performance` |
| Availability | Spring-forward DST gap crashed the query with `ArgumentException` | `fix/availability-correctness-and-performance` |
| Availability | `BookingAvailabilityRepository` always fetched a resource's entire booking history instead of the queried range | `fix/availability-correctness-and-performance` |
| Availability | An incorrect code comment claimed DST-crossing windows had the wrong duration - traced and disproven | `fix/availability-correctness-and-performance` |

## Deferred

Classified by why it wasn't done now, not by urgency - see [open-questions.md](open-questions.md) for
the ones that are genuinely undecided rather than simply not-yet-relevant.

| Item | Class | Why deferred | Revisit when |
|---|---|---|---|
| Database composite tenant foreign keys | Architecture debt | No live exploit path today - every write handler already re-validates the parent through a tenant-filtered lookup. A full migration across every tenant-owned relationship is high blast-radius for a defense-in-depth improvement, not a fix to an active bug. | A new relationship write path is added that doesn't follow the standard tenant-filtered-lookup pattern (see the checklist in [tenant-isolation.md](tenant-isolation.md)). |
| JWT signing key remaining in git history | Security hardening | The exposed key was confirmed never deployed outside local development. Purging it requires a git history rewrite (`git filter-repo` or equivalent) and a force-push, which every existing clone would need to reconcile - disproportionate for a key with no real-world exposure. | Before the repository is shared beyond its current trusted environment, or if evidence ever surfaces that the key was used anywhere real. |
| Logout / session management | Future feature dependency | Doesn't exist at all yet - no logout endpoint, no way to revoke a single session's refresh-token family on demand. | A session-management Work Packet. |
| Absolute refresh-token lifetime | Future feature dependency | Today's refresh window slides indefinitely as long as the token keeps getting refreshed - there's no hard session ceiling. Not necessarily wrong, but undecided. | Same Work Packet as logout - see the open question. |
| Password-change session revocation | Future feature dependency | No password-change flow exists yet to revoke anything from. | Account-security Work Packet. |
| Dynamic role-change invalidation | Future feature dependency | Access tokens are stateless JWTs with roles baked in at issuance; nothing currently changes a user's roles after the fact. | Dynamic role/permission management Work Packet. |
| Approval workflow / capacity semantics for Pending | Future feature dependency | `ApprovalRequest` has a schema (from WP-1) but no Application-layer logic. Current availability treats Pending bookings as capacity-consuming, which is a placeholder assumption, not a designed approval semantic. **Still deferred** after the Booking Work Packet - see the "Booking Approval Workflow" open question. | Approval Work Packet. |
| Booking creation/cancellation | Future feature dependency | Explicitly out of scope for this remediation and every prior WP-3 pass. **Now implemented** - see [bookings-and-concurrency.md](bookings-and-concurrency.md) for the concurrency-safe implementation and its own remaining open questions (TenantAdmin cancellation, approval wiring, idempotency keys). | Done - Booking Work Packet. |
| Rate limiting | Production readiness | Never implemented; no incident or requirement has driven it yet. | Before public/adversarial-traffic exposure. |
| DB health endpoint exposure policy | Production readiness | The existing `/health/db` endpoint's audience (public vs. internal-only) was never explicitly decided. | Before production deployment planning. |
| Correlation ID hardening | Production readiness | Current correlation-id behavior (present on every response including bodyless 401/403) is believed sufficient; no further hardening need has been identified. | If a gap is found in practice (e.g. a response path that doesn't carry it). |

## Test results

Full solution build: clean, 0 warnings, 0 errors. Combined test suite across all four remediation
branches: `BookSpace.Application.Tests` 165/165, `BookSpace.Api.Tests` 79/79,
`BookSpace.Infrastructure.Tests` 21/21 (10 pre-existing + 11 new/updated across this remediation pass,
covering tenant-filter fail-closed behavior, refresh-token rotation concurrency, Resource/BlackoutPeriod
optimistic concurrency, and bounded SQL-Server range filtering - all requiring a real LocalDB instance
rather than the SQLite-backed integration suite).
