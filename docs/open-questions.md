# Open Questions

Genuinely unresolved decisions - not yet answered by any code, comment, or explicit requirement. Do
not treat any of these as settled just because a plausible-sounding default exists in the current code;
where a current default is noted, it is an artifact of what was easiest to build, not a decision.

## Tenant Integrity

**Question**: Should the database eventually enforce tenant-scoped parent/child relationships with
composite tenant foreign keys, rather than relying on every handler re-validating the parent through a
tenant-filtered lookup?

**Context**: Every current foreign key in `BookSpaceModelConfiguration.cs` references a parent's plain
`Id`, not a composite `(TenantId, Id)`. See [tenant-isolation.md](tenant-isolation.md) for the full
current-defense writeup.

**Why it matters**: The current protection is a *convention* every handler must independently follow
correctly, not a schema-enforced guarantee. It has held so far because the convention has in fact been
followed everywhere, but nothing stops a future handler from skipping it.

**Options**: (a) leave as-is, relying on the tenant-isolation-review checklist and code review; (b) add
composite FKs incrementally, one relationship at a time, as new write paths are added; (c) do a single
larger migration across all existing tenant-owned relationships now.

**Trigger**: Before the relationship graph becomes significantly more complex, or when a handler or
repository is added that cannot safely enforce tenant ownership through the current
tenant-filtered-lookup pattern (e.g. a bulk import, a background job constructing entities directly).

**Current default**: (a) - no schema enforcement, convention-only.

---

## JWT Secret in Git History

**Question**: Should git history be rewritten to remove the old, already-exposed development signing
key (present since commit `7b61b72`, WP-2)?

**Context**: The key has been removed from the currently-tracked `appsettings.Development.json` and
replaced with a fresh key in local user secrets (see [authentication.md](authentication.md)), but the
old value remains readable in every historical commit that touched that file.

**Why it matters**: A history rewrite (`git filter-repo` or equivalent) followed by a force-push would
require every existing clone/fork to be re-cloned or carefully reconciled - a disruptive, coordination-
heavy operation.

**Options**: (a) leave history as-is, since the key was confirmed dev-only and never deployed; (b)
rewrite history now regardless, as a matter of hygiene; (c) rewrite it only if triggered.

**Trigger**: Before the repository is distributed beyond its current trusted environment, or if
evidence ever surfaces that the exposed key was used anywhere real.

**Current default**: (a) - left in history, not rewritten, per explicit confirmation this session that
the key was never used outside local development.

---

## Session Security

**Question**: What should the absolute refresh-token/session lifetime be (today it slides indefinitely
as long as refresh keeps happening)? Should logout revoke one session, every session for the user, or
offer both? When should a password change revoke outstanding refresh-token families?

**Context**: None of logout, absolute session lifetime, or password-change revocation exist in the
codebase today - see [authentication.md](authentication.md)'s "Deferred session-management decisions."

**Why it matters**: These are genuine security-policy decisions with real UX tradeoffs (a hard session
ceiling improves security but forces periodic re-login even for active users; single- vs. all-session
logout affects multi-device users differently).

**Trigger**: A session management / logout / account-security Work Packet.

**Current default**: none - these behaviors are simply absent, not defaulted to a particular answer.

---

## Dynamic Authorization

**Question**: What happens to an already-issued access token when a user's role changes mid-session?

**Context**: Access tokens are stateless JWTs with roles baked in at issuance (`AccessTokenMinutes`,
currently 15). There is no mechanism today to invalidate one early.

**Why it matters**: A short token lifetime already bounds the exposure window, but if role changes ever
need to take effect faster than that, a different mechanism (e.g. a token-version claim checked against
a stored value) would be needed.

**Trigger**: When role/permission management becomes dynamic (i.e. an admin can change another user's
roles at runtime, as opposed to only at account creation).

**Current default**: none - a role change simply doesn't propagate until the current access token
naturally expires.

---

## Resource Reactivation

**Question**: Should an Archived resource ever be reactivable, and if so, should that be a dedicated
lifecycle action rather than a side effect of the general Update command?

**Context**: See [resource-lifecycle-and-capacity.md](resource-lifecycle-and-capacity.md) - Archived is
currently modeled as terminal; Update explicitly rejects any edit to an Archived resource.

**Why it matters**: If the business need for reactivation is real, building it as an explicit action
(with its own validation - e.g. should its old name still be available? should old
rules/blackouts/approvers still apply?) is a different design exercise than just relaxing Update's
check.

**Trigger**: When the business actually requires restoring an archived resource, not before.

**Current default**: no reactivation path exists at all.

---

## Booking Concurrency

**Question**: What is the actual transactional source of truth for capacity when two booking-creation
requests race for the same resource/time window?

**Context**: The live availability query (see [availability-and-timezones.md](availability-and-timezones.md))
is explicitly a read model with no locking behind it - it can report a slot as available at query time
with no guarantee it still will be moments later. Today's only concurrency-relevant guard is the
capacity-reduction check on `UpdateResource` (see
[resource-lifecycle-and-capacity.md](resource-lifecycle-and-capacity.md)), which protects a different
write path entirely.

**Why it matters**: Building Booking-create on the unstated assumption that the availability query
itself prevents overbooking would reintroduce exactly the kind of race this remediation pass fixed
elsewhere (RowVersion for Resource/RefreshToken).

**Options**: application-level re-check inside a transaction at commit time (re-summing capacity),
`SERIALIZABLE` isolation, `UPDLOCK`/`HOLDLOCK` table hints, or an optimistic-concurrency token on a
per-slot aggregate if one gets introduced.

**Trigger**: The Booking-create Work Packet, before any implementation begins.

**Current default**: none - this is explicitly unresolved and must not be assumed answered.

---

## Idempotency

**Question**: Should booking creation support client-supplied idempotency keys (to safely retry a
request that timed out without risking a duplicate booking)?

**Context**: No booking-creation endpoint exists yet to need one.

**Trigger**: Booking API design, alongside the concurrency question above.

**Current default**: none.

---

## Approval Semantics

**Question**: How should a Pending approval affect capacity - does a pending booking reserve capacity
the same as a confirmed one? What happens to reserved capacity when an approval expires or is rejected?

**Context**: `ApprovalRequest` has a schema (from WP-1) but zero Application-layer logic today. The
current availability query treats `Pending` bookings as capacity-consuming (same as `Confirmed`) - this
was a deliberate placeholder decision ("you should not be able to double-book a slot while someone
else's approval is pending"), not a designed approval-lifecycle semantic, since there is no approval
lifecycle implemented yet to design around.

**Trigger**: The Approval/Booking Work Packet.

**Current default**: Pending consumes capacity identically to Confirmed; there is no expiry/rejection
handling to release it, because nothing creates a Pending booking through the API yet.
