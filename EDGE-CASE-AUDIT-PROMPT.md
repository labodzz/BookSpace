We are doing a PROJECT-WIDE BUSINESS-LOGIC SANITY AUDIT of BookSpace - not a security audit, not a
test-coverage audit, not a release-readiness matrix (those are separate exercises, see
`WP3-AUDIT-PROMPT.md` for that style if it still exists). This one hunts a narrower, easier-to-miss
class of defect.

Act as a senior .NET backend engineer doing an adversarial logic review.

THE SPECIFIC DEFECT CLASS TO HUNT

Code that is technically correct - it compiles, passes every validator, throws no exception, has
green tests - but permits or forbids something that makes no real-world sense given what the feature
is actually for. This is NOT the same as a bug, a missing validation, or a security hole; it's a gap
between "what the code allows" and "what a human would expect to be allowed" that nobody happened to
type out as an explicit requirement, so nothing ever rejected it.

Calibrating example (already found and fixed this session): `CreateBlackoutPeriodCommandRequest`
only validated `EndUtc > StartUtc` - nothing stopped `StartUtc` from being yesterday. Creating a
"blackout" for a period that has already elapsed is meaningless (nothing is being blocked, since the
time already passed) and is actively confusing next to real historical booking data for that same
window. No test caught it because every existing test happened to use future dates. The fix was one
`Must(startUtc => startUtc >= DateTimeOffset.UtcNow)` rule - trivial once seen, invisible until
someone asked "wait, why would you ever want this in the past?" instead of "does this validate
correctly?"

That is the bar: for every field, every state transition, every cross-entity relationship, ask "what
would a reasonable person expect here, and does the code actually enforce that, or just something
adjacent to it?" - not "does this throw an exception."

SCOPE - the whole project, not one work package

Walk every feature currently implemented, reading the ACTUAL current code (not AI-USAGE.md's prose,
not a prior audit's memory of it - things have changed: ResourceType now has full CRUD, blackout
periods now reject a past `StartUtc`, etc.):

- `backend/BookSpace.Application/Auth/**` (login, refresh-token rotation/reuse-detection, logout)
- `backend/BookSpace.Application/Users/**`
- `backend/BookSpace.Application/Resources/**` (Resource, AvailabilityRule, BlackoutPeriod,
  ResourceApprover, the live availability query, `IntervalMath.cs`)
- `backend/BookSpace.Application/ResourceTypes/**`
- Every EF configuration/constraint in `BookSpaceModelConfiguration.cs` relevant to the above
- Every controller under `backend/BookSpace.Api/Controllers/`

For each entity/feature, actually trace: validator -> handler -> repository -> DB constraint. Read
the real files each time; do not reason from what you'd expect a well-built CRUD feature to do.

CHECKLIST OF SMELLS TO APPLY TO EVERY ENTITY

For each entity found in scope, deliberately run through these angles. Not all will apply to every
entity - say so when one doesn't, don't force a finding.

1. Time/date logic
   - Can a "future-only" concept (a block, a booking, a schedule) be created or edited into the past?
   - Can something be created/edited into a range that's already logically closed (e.g. editing a
     resource's schedule for a day that's already over)?
   - Do overlapping/adjacent ranges behave consistently between Create and Update for the same entity?
   - Timezone/DST edge cases: are they at least consistently documented, not silently different
     between two code paths that should agree?

2. State machines / status fields
   - List every enum-backed status field. For each, what transitions does the code actually allow
     (not what you'd assume)? Is there any transition that's technically reachable but nonsensical
     (e.g. reactivating something that was terminally closed, re-approving something already
     rejected, editing a field that should be frozen once a status is reached)?
   - Idempotency: does re-applying a terminal action (archive, cancel, reject) behave sanely the
     second time, or does it error/duplicate/silently do something different?

3. Quantity / capacity / numeric fields
   - Zero, negative, and boundary values - are they rejected where it matters and allowed where it's
     legitimate (don't assume "reject negative everywhere" is always correct)?
   - Does decreasing a capacity/limit below an amount already committed elsewhere get checked, or
     silently allowed to create an inconsistent state?
   - Do quantities/amounts from two different sources that should agree (e.g. a stored total vs. a
     sum of parts) ever have a path where they can drift apart?

4. Uniqueness / idempotency assumptions
   - Does a "soft delete" (archive, deactivate, etc.) actually free up whatever uniqueness constraint
     depended on that row, or does the old row silently keep reserving a name/slot forever? Is that
     the intended behavior, or an accident of how the unique index was declared?
   - Two near-simultaneous requests for the same "shouldn't exist twice" thing - does the DB
     constraint actually back up the application-level check, or is the application check the only
     guard?

5. Cross-entity / referential consistency
   - When one entity implies a requirement on another (e.g. "requires approval" implies "should have
     at least one approver"), is that invariant enforced anywhere, or can the two drift out of sync
     (e.g. the last approver removed while approval is still required)?
   - Deleting/archiving a parent - does every dependent child end up in a sane state, not just "no FK
     violation"?
   - A composite route/command referencing two IDs (parent + child) - does a mismatched pairing
     (child exists, but under a different parent) get rejected, or does it silently operate on the
     wrong row?

6. Authorization edge cases beyond the obvious role check
   - Combined roles that individually wouldn't pass (e.g. Member + Approver but not TenantAdmin) -
     still correctly blocked from admin-only actions?
   - Any action where "you can see it" and "you can act on it" have different implied audiences that
     the code doesn't actually distinguish?

7. Concurrency-shaped nonsense
   - Two requests that are each individually valid, but whose *combination* produces a state nobody
     would want (not just a duplicate-row race - a logical race, e.g. two edits that each pass
     validation individually but together violate an unstated invariant).

METHOD

- Don't grep for missing validation calls and stop there - that only finds the mechanical half. Read
  the handler and actually simulate a user causing the scenario, then check whether a test already
  proves the outcome one way or the other.
- When you find something that's ambiguous whether it's a real gap or an intentional simplification,
  say so explicitly and ask rather than assuming either way - do not invent stricter business rules
  than the code and existing docs actually support, but do not wave away something that plainly
  doesn't make sense just because nothing crashes.
- Prefer depth over breadth: it is better to trace five real user-triggerable scenarios per entity all
  the way through validator -> handler -> DB than to list twenty superficial "what if X is null"
  guesses.

OUTPUT FORMAT

For each finding:
- Entity / file / line.
- The exact scenario: what a user (or two concurrent users) would do to trigger it.
- Why it's illogical or risky - what a reasonable person would expect instead.
- Suggested fix direction (one or two sentences - not a full implementation).
- Your confidence: is this clearly a gap, or a judgment call worth confirming with the user first?

Group findings by entity/feature, most-concerning first within each group. If a whole feature comes
back clean, say so plainly instead of padding the report with nitpicks.

CONSTRAINTS

- Read-only investigation. Do not modify production code during the audit itself.
- Do not commit, push, or create a PR.
- Do not invent requirements that were never intended - if something is a deliberate, already-accepted
  simplification (check code comments and AI-USAGE.md before flagging it as new), say so and skip it
  rather than re-raising it as a fresh finding.
- After the report, wait for the user to say which findings to actually fix before changing any
  production code - this is a find-and-report pass, not a fix-everything pass.
