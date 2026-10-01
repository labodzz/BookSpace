# Background jobs

The hosted-service *foundation* for WP-8 (reminders, no-show handling, stale approvals, an ICS feed,
...). **This branch does not implement any of those jobs.** It only builds the minimal, correctly
structured scaffold later WP-8 branches will plug real jobs into: the periodic loop, the per-cycle
dependency scope, and cancellation/shutdown handling.

## Pieces

- **`BackgroundJobsWorker`** (`BookSpace.Api/BackgroundJobs`) - a `BackgroundService`. Owns the periodic
  loop and the app's cancellation token; does nothing domain-specific itself.
- **`IBackgroundJobCycle`** (`BookSpace.Application/BackgroundJobs`) - "one unit of periodic work."
  Real WP-8 jobs will be implementations of this interface, resolved from DI like any other Application
  service.
- **`NoOpBackgroundJobCycle`** - the only implementation this branch ships. Logs a debug line and does
  nothing else - no database access, no external calls. Proves the loop/scope/cancellation plumbing
  actually runs without inventing business logic ahead of the jobs that will need it.
- **`BackgroundJobsOptions`** / **`BackgroundJobsOptionsValidator`** - configuration and its startup
  validation.

## Why the hosted service is a singleton

`IHostedService`/`BackgroundService` instances are always singletons in the generic host - there is
exactly one `BackgroundJobsWorker` for the lifetime of the process, started once at app startup and
stopped once at shutdown. It is registered with `AddHostedService<BackgroundJobsWorker>()`, the same
mechanism ASP.NET Core uses for any other hosted service.

## Why it creates a new DI scope per cycle

A singleton must never hold a scoped dependency (a `DbContext`, a repository, anything the rest of this
codebase already treats as scoped) directly - doing so would either leak across requests/cycles or
throw outright. Real WP-8 jobs will need exactly those kinds of scoped dependencies (a job that reads
pending reminders needs a repository; a repository needs a `DbContext`). So instead of injecting
anything scoped into `BackgroundJobsWorker` itself, it injects `IServiceScopeFactory` (itself a
singleton, safe to hold) and creates a fresh `IServiceScope` inside `RunCycleAsync` for every single
cycle, resolving `IBackgroundJobCycle` from that scope and disposing the scope immediately afterward.
This is the same pattern Microsoft's own hosted-service documentation recommends for exactly this
reason, and it means a future real `IBackgroundJobCycle` implementation can freely depend on scoped
services without `BackgroundJobsWorker` itself ever changing.

## The loop, briefly

```text
while not cancelled:
    run one cycle in a fresh DI scope (a cycle that throws is logged and skipped - not fatal)
    wait for the poll interval, or until cancelled - whichever comes first
```

The wait uses `Task.Delay(interval, TimeProvider, cancellationToken)`, not
`System.Threading.Timer` and not a job scheduling library (Hangfire/Quartz) - both are explicitly out
of scope for this foundation. `TimeProvider` (injected, defaulting to `TimeProvider.System` in
production) is what lets tests substitute a `FakeTimeProvider` and advance time instantly instead of
waiting out real seconds - see `BackgroundJobsWorkerTests`.

Cancellation (application shutdown) interrupts the wait immediately: `Task.Delay` throws
`OperationCanceledException`, which `BackgroundJobsWorker` catches specifically to break the loop and
return cleanly, rather than propagating as an unhandled fault.

## Configuration

```json
"BackgroundJobs": {
  "Enabled": false,
  "PollIntervalSeconds": 30
}
```

- **`Enabled`** (default `false`) - the worker is registered as a hosted service either way, but its
  `ExecuteAsync` returns immediately without ever resolving or running a cycle when this is `false`.
  Off by default: there is no real job for it to run yet, so there is no reason for it to be on in
  Development, Testing, or an unconfigured deployment.
- **`PollIntervalSeconds`** (default `30`) - must be a positive number of seconds.
  `BackgroundJobsOptionsValidator` is registered against `ValidateOnStart()` (see `Program.cs`), so a
  zero or negative value fails application startup immediately with a clear message, instead of the
  worker silently busy-looping or only failing once it actually tries to poll.

`BatchSize` (or any other real-job-shaped setting) is deliberately not introduced here - this branch
processes nothing, so there is nothing yet for a batch size to bound. That belongs in whichever future
WP-8 branch adds the first real `IBackgroundJobCycle`.

## Job lease lock

A later WP-8 branch than the foundation described above. Its goal: if BookSpace ever runs more than one
application instance at once, only one of them may run a given background job's cycle at a time.

```text
Instance A ─┐
            ├── both try to run the SAME job's cycle
Instance B ─┘
```

Without a lock, both instances would run the (eventually real) cycle concurrently - for reminders or
no-show handling that means duplicate work at best and duplicate side effects (two reminder emails, two
no-show cancellations) at worst. The lock does not implement any of those jobs; it only decides which
single instance is allowed to run *a* cycle for a given job name on a given poll.

### What this guarantees, and what it does not

**Guarantees:**
- At most one instance holds an active lease for a given `JobName` at any moment - enforced physically by
  `JobName` being the `JobLeases` table's primary key, not just application-level care.
- A lease always has an expiry (`LeaseExpiresAtUtc`), so an instance that acquired a lease and then
  crashed (or lost network to the database, or whose container was killed) never leaves the job
  permanently stuck - another instance can take over once the lease lapses.
- Acquiring, renewing, and releasing are each a single atomic database statement - two instances racing
  for the same job can never both believe they hold it.

**Does NOT guarantee:**
- **Exactly-once execution.** A lock reduces *concurrent* processing; it says nothing about a single
  instance's own reliability. An instance can successfully acquire the lease, perform a real side effect
  (send a reminder, cancel a no-show booking), and then crash *before* releasing or even recording that it
  did so. When another instance later takes over the (by-then expired) lease, nothing here tells it "that
  work was already done." **Lease lock is not a substitute for idempotency.** Whatever real job a future
  WP-8 branch builds on top of this (reminders, no-show handling, ...) will still need its own
  idempotency guard - typically a database unique constraint or an outbox/processed-marker table keyed on
  the specific piece of work (e.g. "reminder already sent for booking X"), not on the job cycle as a
  whole. The lock only bounds *how many instances can be mid-cycle at once* (at most one); it says nothing
  about whether a single side effect within that cycle has already happened.
- Fairness or ordering between instances - whichever instance's acquire attempt wins a given poll is not
  specified or guaranteed to rotate.

### Pieces

- **`JobLease`** (`BookSpace.Domain.Entities`) - `JobName` (primary key), `OwnerId`, `LeaseExpiresAtUtc`,
  `LastHeartbeatAtUtc`. Not `ITenantOwned`: which instance is running a job cycle is a global
  infrastructure concern, not tenant-scoped domain data, so it is never subject to
  `BookSpaceDbContext`'s tenant query filter.
- **`IJobLeaseStore`** (`BookSpace.Application.BackgroundJobs`) / **`JobLeaseStore`**
  (`BookSpace.Infrastructure.Persistence`) - the three atomic, single-round-trip database primitives
  (`TryAcquireAsync`, `TryRenewAsync`, `TryReleaseAsync`) everything else is built from.
- **`IBackgroundJobInstanceIdentity`** / **`BackgroundJobInstanceIdentity`** - this process's stable
  `OwnerId` for its entire lifetime.
- **`IJobLeaseCoordinator`** / **`JobLeaseCoordinator`** - wraps the store with the heartbeat loop and
  owner identity; returns an `IJobLease` handle on a successful acquire.
- **`BackgroundJobsWorker`** - acquires the lease for `PrimaryCycleJobName` before every cycle, skips the
  cycle entirely when it doesn't get it, and releases it (via disposing the handle) once the cycle ends.

### Owner identity

Every acquire/renew/release call needs a stable identifier for *this running instance* -  `OwnerId`.
`BackgroundJobInstanceIdentity` builds it once, at construction (it is registered as a singleton, so this
happens exactly once per process), as:

```text
{InstanceName-or-MachineName}:{ProcessId}:{a fresh Guid}
```

A machine/container name alone is not enough - two processes can run on the same host (two containers on
one VM, or two `dotnet run` instances on a developer's machine), and a lease keyed only on hostname would
let them collide. Appending the process id and a per-startup GUID makes `OwnerId` unique even for two
instances started on the same host at the same instant. It stays the same for every lock attempt this
instance ever makes, and a brand new one is generated the next time the process starts - there is
deliberately no way for a restarted instance to "resume" a lease it held before restarting; it must
acquire fresh like any other instance would.

### Clock authority

Every expiry decision (is a lease still valid? has it expired and eligible for takeover?) is made using
`SYSUTCDATETIME()` - SQL Server's own UTC clock - not any application instance's local
`DateTime.UtcNow`. Two application instances' local clocks can drift or be skewed relative to each other;
comparing one instance's local time against a row another instance wrote would make "is this expired"
depend on whose clock is right. Anchoring every comparison to the single, shared, authoritative database
clock removes that question entirely - every instance agrees because they are all asking the same clock,
not comparing their own.

### Acquire is atomic

`TryAcquireAsync` never does a separate "check if free, then write" - both steps a caller could otherwise
race between. It is one conditional `UPDATE`:

```sql
UPDATE JobLeases
SET OwnerId = @ownerId, LeaseExpiresAtUtc = DATEADD(SECOND, @leaseDurationSeconds, SYSUTCDATETIME()), LastHeartbeatAtUtc = SYSUTCDATETIME()
WHERE JobName = @jobName AND (OwnerId = @ownerId OR LeaseExpiresAtUtc <= SYSUTCDATETIME());
```

which steals an already-expired lease or safely re-acquires this same owner's still-valid one, in one
atomic statement SQL Server evaluates and applies per-row as a single operation. When no row exists yet
for the job at all, that `UPDATE` matches nothing, and `TryAcquireAsync` falls back to a guarded `INSERT`:

```sql
INSERT INTO JobLeases (JobName, OwnerId, LeaseExpiresAtUtc, LastHeartbeatAtUtc)
SELECT @jobName, @ownerId, DATEADD(SECOND, @leaseDurationSeconds, SYSUTCDATETIME()), SYSUTCDATETIME()
WHERE NOT EXISTS (SELECT 1 FROM JobLeases WHERE JobName = @jobName);
```

The `WHERE NOT EXISTS` guard narrows the race but cannot close it alone - two callers can both evaluate it
as true before either commits. The real backstop is `JobName` being the table's **primary key**: if two
instances' `INSERT`s do race for the same still-missing row, exactly one commits and the other gets a
primary-key violation (SQL error 2627, or 2601 for the equivalent unique-index case) - `JobLeaseStore`
catches specifically that and returns "not acquired," never letting it surface as an application error or
crash the worker. This is the "unique constraint as the last physical safety net" the acquire protocol
described in the WP-8 task relies on. No `RowVersion`/concurrency token is needed on `JobLease`: the
conditional `UPDATE`'s `WHERE` clause is itself the atomic compare-and-swap, and the primary key is what
protects the `INSERT` fallback.

### Heartbeat

While a cycle is running, `JobLeaseCoordinator`'s lease handle runs a background loop that calls
`TryRenewAsync` every `HeartbeatIntervalSeconds`:

```sql
UPDATE JobLeases
SET LeaseExpiresAtUtc = DATEADD(SECOND, @leaseDurationSeconds, SYSUTCDATETIME()), LastHeartbeatAtUtc = SYSUTCDATETIME()
WHERE JobName = @jobName AND OwnerId = @ownerId AND LeaseExpiresAtUtc > SYSUTCDATETIME();
```

This only succeeds while `OwnerId` still matches *and* the lease has not already expired - a stale owner
(one whose lease already lapsed, whether or not anyone else has taken it over yet) can never resurrect its
old lease through this path. When a renewal fails, the heartbeat loop:
1. Logs a warning ("job lease heartbeat lost").
2. Cancels `IJobLease.LeaseLostToken`.
3. Stops the loop - there is no point continuing to heartbeat a lease this instance no longer holds.

`BackgroundJobsWorker` links `LeaseLostToken` with its own shutdown token
(`CancellationTokenSource.CreateLinkedTokenSource`) before running the cycle, so a real, cancellation-aware
future job would observe the loss immediately and unwind, rather than continuing to run work under a lock
it no longer holds. `BackgroundJobsOptionsValidator` enforces `HeartbeatIntervalSeconds <
LeaseDurationSeconds` at startup, so there is always at least one full heartbeat interval of margin before
a single slow/delayed renewal could cost the instance its lease; the shipped defaults (a 20s heartbeat
against a 60s lease) leave a 3x margin, which is a reasonable starting point, not a hard requirement -
what matters is that the gap is wide enough that one occasional delayed heartbeat isn't itself a lease
loss.

### Release

`BackgroundJobsWorker` disposes the lease handle in a `finally`-equivalent position (an `await using`
around the cycle) once the cycle ends, however it ended. Disposing:
1. Stops the heartbeat loop and awaits it.
2. Calls `TryReleaseAsync`, which deletes the row **only if `OwnerId` still matches**:
   ```sql
   DELETE FROM JobLeases WHERE JobName = @jobName AND OwnerId = @ownerId;
   ```

The `OwnerId` condition means one instance can never release (or otherwise disturb) a lease another
instance now legitimately owns - if this instance's lease was already lost and taken over before release
runs, the `DELETE` simply matches nothing (0 rows), which is logged as "release ignored - not the current
owner," not treated as an error.

### Crash scenario

None of the above depends on release actually running. If the process or container dies outright, no
`finally`/`await using` ever executes, and the `JobLease` row is left exactly as the heartbeat last wrote
it - with a `LeaseExpiresAtUtc` that is now approaching. Once that instant passes, `TryAcquireAsync`'s
`UPDATE` (the `OwnerId = @ownerId OR LeaseExpiresAtUtc <= SYSUTCDATETIME()` branch) lets any other instance
steal the row on its very next poll. The lock can delay a takeover by at most one lease duration after the
last successful heartbeat - it can never get permanently stuck.

### Configuration

```json
"BackgroundJobs": {
  "Enabled": false,
  "PollIntervalSeconds": 30,
  "LeaseDurationSeconds": 60,
  "HeartbeatIntervalSeconds": 20
}
```

- **`LeaseDurationSeconds`** (default `60`) - how long an acquired lease stays valid without a
  successful heartbeat before it is eligible for takeover.
- **`HeartbeatIntervalSeconds`** (default `20`) - how often the current owner renews its lease while a
  cycle is running. `BackgroundJobsOptionsValidator` rejects a value that is not strictly less than
  `LeaseDurationSeconds` (and rejects either value being zero or negative) - fails application startup
  immediately, the same `ValidateOnStart()` mechanism `PollIntervalSeconds` already used.
- **`InstanceName`** (optional) - human-readable label folded into this instance's `OwnerId` (see "Owner
  identity" above); falls back to `Environment.MachineName` when unset.

## Notification outbox

A later WP-8 branch than the lease lock above. Its goal: every logical notification (a booking
confirmation, a cancellation, a reminder, ...) is durably recorded in the database **exactly once**, even
when the command/job that decided to send it is retried, two application instances race to create the
same notification, the application restarts mid-decision, or a genuine concurrency race occurs. **This
branch does not send any email.** It only persists the *decision* "this notification should eventually be
sent" exactly once - a later sender task reads rows this branch creates and actually delivers them.

### Why an in-memory flag (or a plain "did I already do this" check) is not enough

A naive approach - keep a `HashSet<string>` of already-sent keys in memory, or do a `SELECT` to check
"does this notification already exist" before an `INSERT` - looks correct for a single request on a
single instance, but fails in exactly the scenarios this task is required to survive:

- **An in-memory flag is lost on restart.** The whole point of "durable" is that the record survives the
  process that created it dying immediately afterward - an in-memory set cannot.
- **An in-memory flag is per-instance.** Two application instances (the whole reason the WP-8 lease lock
  exists) each have their own memory - instance A's flag says nothing about what instance B has decided.
- **`SELECT exists, then INSERT` is a classic TOCTOU race.** Two concurrent callers (two requests, two
  instances, a retried command racing the original) can both run the `SELECT`, both see "doesn't exist
  yet," and both proceed to `INSERT` - exactly the anti-pattern the WP-8 lease lock task already called
  out for job-lease acquisition, and the same fix applies here: the database's own unique constraint, not
  an application-level check, has to be the actual guarantee.

### How `IdempotencyKey` prevents a duplicate record

`IdempotencyKey` (`NotificationOutboxItem`, `BookSpace.Domain.Entities`) is a deterministic string that
identifies **one specific logical notification**, not just "a notification for this booking" - e.g.
`booking:{bookingId}:confirmation` (one booking has exactly one confirmation) vs.
`booking:{bookingId}:reminder:{scheduledUtc}` (one booking can have several distinct reminders, one per
scheduled instant, each its own key). A caller that re-decides to enqueue the same logical notification -
because a command was retried, a job cycle reran, or two instances both reached the same decision -
always computes the *same* key, so the database can recognize "this exact thing was already recorded,"
not just "something for this booking exists." **No business event is wired to produce these keys in this
branch** - the examples above describe a future caller's intent, not anything this branch's code
constructs or depends on.

### Why the unique constraint is the real guarantee, not `IdempotencyKey` alone

A string being "deterministic" only means two *computations* of it agree - it says nothing about what
happens when two callers holding that same computed value both try to persist a row at the same instant.
That guarantee comes from `IX_NotificationOutboxItems_TenantId_IdempotencyKey`, a **unique index** on
`(TenantId, IdempotencyKey)` (see "Tenant scope of the unique constraint" below for why that pair, not
`IdempotencyKey` alone). `NotificationOutboxWriter.EnqueueAsync` never does "check, then insert": it always
attempts the insert directly, and lets the database decide:

```text
INSERT the row
    succeeds -> this caller created it: EnqueueOutcome.Created
    fails with SQL error 2601/2627 (duplicate key) -> someone already recorded it: EnqueueOutcome.AlreadyExists
    fails with anything else -> a real, unexpected failure - propagates, never reported as a duplicate
```

Two concurrent callers racing for the identical `(TenantId, IdempotencyKey)` therefore always resolve to
exactly one row: one insert commits, the other's commit is physically rejected by the index and is
reported back as the normal, expected `AlreadyExists` outcome - never an unhandled exception, and never a
second row. `EnqueueAsync` catches *only* SQL errors 2601 (unique index) and 2627 (unique/primary key
constraint) - any other `DbUpdateException` (a foreign-key violation, a transient connection failure, ...)
propagates untouched, so a genuinely unexpected database error can never be silently misreported as "this
was already enqueued." After a caught duplicate-key failure, the failed entity is explicitly detached
from the `DbContext`'s change tracker - EF Core leaves a failed `SaveChangesAsync`'s entities tracked as
`Added` otherwise, which would make the *next* `SaveChangesAsync` on that same (shared, scoped) `DbContext`
try to re-insert the identical row and fail identically, or get bundled into an unrelated caller's next
commit.

### Clock convention - not the lease lock's SQL-authoritative clock

`CreatedAtUtc` and the default `AvailableAtUtc` are written using this application instance's own
`DateTimeOffset.UtcNow` - the ordinary convention every other timestamp in this schema already follows
(`Booking.CreatedAtUtc`, `ApprovalRequest.RequestedAtUtc`, ...), **not** the lease lock's
`SYSUTCDATETIME()`-only rule. That distinction is deliberate, not an oversight: the lease lock needed the
database's own clock specifically because two *different instances* compare a lease's expiry against each
other's writes to decide mutual-exclusion ownership - any clock skew between instances could make them
disagree about who owns the lock. Nothing about the outbox depends on two instances agreeing on exactly
when "now" was at enqueue time; a few seconds of skew in `AvailableAtUtc` only shifts a future scheduling
decision by a few seconds, never a correctness or ownership question. The actual idempotency guarantee
here comes entirely from the unique index, not from any clock.

### Tenant scope of the unique constraint

The constraint is on **`(TenantId, IdempotencyKey)`**, not `IdempotencyKey` alone - matching this schema's
existing convention for tenant-owned uniqueness (`Resource` is unique per `(TenantId, Name)`,
`ResourceType` per `(TenantId, Name)`, not globally). `User.Email` is the one deliberate exception, and
only because login must resolve a user by email *before* any tenant is known - there is no comparable
reason for a notification idempotency key to ever need comparing across tenants. In practice, every
example key given above already embeds a globally-unique `bookingId` GUID, so a cross-tenant collision
could not happen today regardless - but the constraint does not rely on that being true of every future
key format. Scoping to `(TenantId, IdempotencyKey)` stays correct even for a hypothetical future
notification type whose key is built from something less inherently unique than a GUID.

### How this will share a transaction with a future business write

`NotificationOutboxWriter.EnqueueAsync` calls `SaveChangesAsync` itself, which looks like a departure from
the rest of this codebase's repository convention (`AddAsync` staged, a separate caller-controlled
`SaveChangesAsync` later - see `BookingRepository`, `ResourceRepository`, etc.). This is deliberate:
`EnqueueAsync`'s whole contract is to answer, immediately and definitively, "was a new row created or did
one already exist" - an answer only the database's own constraint can give, not something that can be
deferred to some later, caller-controlled commit.

That said, every repository in this codebase - including `NotificationOutboxWriter` - operates on the
*same shared, scoped `BookSpaceDbContext`* within one request/handler (see `CreateBookingCommandHandler`:
`bookingRepository.AddAsync(booking, ct)` and `approvalRequestRepository.AddAsync(approval, ct)` both just
stage changes; a single later `bookingRepository.SaveChangesAsync(ct)` commits both together, because
there is no separate per-repository connection or transaction - the "unit of work" in this codebase *is*
the shared `DbContext`). The same mechanism gives a future business handler exactly the atomic commit this
task requires, with no explicit transaction wrapper needed:

```csharp
// Future handler (NOT implemented by this task):
await bookingRepository.AddAsync(booking, cancellationToken);           // staged, not yet saved
var enqueueResult = await notificationOutboxWriter.EnqueueAsync(        // this call's own SaveChangesAsync
    confirmationRequest, cancellationToken);                            // commits BOTH rows together
```

Because `bookingRepository.AddAsync` only stages the booking (it does not call `SaveChangesAsync` itself),
and `EnqueueAsync` is called afterward on the *same* `DbContext`, `EnqueueAsync`'s own `SaveChangesAsync`
persists the new `Booking` row and the new `NotificationOutboxItem` row in one transaction - exactly the
"booking confirmed + confirmation enqueued = one atomic commit" requirement. If that `SaveChangesAsync`
fails for any reason, neither row is persisted; there is no possible outcome where the booking is
confirmed but the notification silently never existed, or vice versa.

**A known, currently-accepted limitation of this mechanism**: `EnqueueAsync`'s narrow catch
(`SqlException.Number is 2601 or 2627`) cannot yet distinguish "my own `(TenantId, IdempotencyKey)`
constraint fired" from "some other unique constraint on an entity staged earlier in the same
`SaveChangesAsync` call also fired at the same moment" - both currently resolve to
`EnqueueOutcome.AlreadyExists`, which would be the wrong answer in the (today, impossible, since nothing
calls `EnqueueAsync` from a handler yet) scenario where the business entity's *own* uniqueness check is
what actually collided. Whichever future task wires a real business handler into this writer should
revisit this if the business write it is combined with has its own unique constraint capable of
colliding at the same instant.

### What this task guarantees - and does not

> **Guarantee: one durable outbox row per `(TenantId, IdempotencyKey)`.**

That is the entire guarantee. It does **not** mean an email is ever sent, and it does **not** mean
"exactly-once email delivery." A future sender task can:
1. Read a `Pending` row.
2. Successfully call an external email provider.
3. Crash, or lose its database connection, **before** updating that row to `Sent`.

The row is still `Pending` afterward, and a retry processor will correctly try again - which means the
*email* could be sent twice, even though the *outbox row* was created exactly once. **A durable,
uniquely-keyed record of "this should be sent" is not the same thing as "this was sent exactly once."**
Closing that remaining gap is the sender task's responsibility, not this one's - typically via the email
provider's own idempotency key support where available (many providers accept a client-supplied dedup
key), plus carefully documenting the actual delivery semantics (at-least-once, with the possibility of a
rare duplicate email, is a normal and often-accepted outcome for this kind of system - "exactly-once
delivery" across an unreliable external call is not achievable by the outbox alone, only "exactly-once
intent to deliver").

### Tenant behavior for a future background reader

`NotificationOutboxItem` is `ITenantOwned` - an ordinary web request reading it is correctly scoped by
`BookSpaceDbContext`'s global tenant query filter, exactly like every other tenant-owned entity (see
`docs/tenant-isolation.md`). A future retry/batch processor, however, is a background worker with **no**
`ICurrentUserContext.TenantId` (no HTTP request, no JWT claim) - and the tenant filter *fails closed* on a
null tenant context, meaning it would see **zero** rows, not every tenant's rows, if it queried this table
through the ordinary filtered path. That processor must read due items the same explicitly-unscoped way
`IUserRepository.FindByEmailAsync` already does for its own narrow, justified reason (see
`docs/tenant-isolation.md`'s exception table): a dedicated repository method using `.IgnoreQueryFilters()`
to see every tenant's due items in one query, while every other, ordinary (web-request-scoped) read of
this table keeps going through the normal filtered path with no special handling. This task does not add
that repository method (no batch processor exists yet - see below) - this section exists so the next task
that adds one does not have to rediscover why the plain filtered path cannot work for it.

### Due-item index

`IX_NotificationOutboxItems_AvailableAtUtc` is filtered to `Status = 'Pending'` (the same filtered-index
pattern `Resource`'s and `Invitation`'s unique indexes already use elsewhere in this schema), so it
supports exactly the future retry processor's query shape - due, unsent items, oldest first, in a bounded
batch:

```sql
SELECT TOP (@batchSize) *
FROM NotificationOutboxItems
WHERE Status = 'Pending' AND AvailableAtUtc <= SYSUTCDATETIME()
ORDER BY AvailableAtUtc ASC;
```

The index shrinks as items transition to `Sent` and fall out of the filter, rather than growing
unbounded forever as the table accumulates history.

### What this task does not implement

An email provider, actually sending any email, the reminder/confirmation/cancellation/rejection event
integrations that would call `EnqueueAsync` (the `IdempotencyKey` examples above describe future intent
only - no booking handler constructs one yet), a batch/retry processor that reads `Pending` rows, any
retry/backoff execution, a distributed claim mechanism for an individual outbox row (the WP-8 lease lock
only ever protected one *job cycle* at a time, never an individual row within it), the no-show job, the
stale-approval job, an ICS feed, or any frontend change. All of that is future WP-8 work built on top of
the `NotificationOutboxItem` table and `INotificationOutboxWriter` this task adds.

## Due-batch query

A later WP-8 branch than the outbox table above. Its goal: read due notification-outbox work in bounded,
configurable batches, using the database to do the filtering/ordering/limiting - never "load everything,
then filter in application code."

### What "due" means, precisely

Using the actual `NotificationOutboxItem` model (see "Notification outbox" above - nothing here invents a
field or status that model does not have), an item is due when, at the moment of the query:

- `Status == NotificationOutboxStatus.Pending` - not yet sent. (`NotificationOutboxStatus` currently has
  exactly two members, `Pending` and `Sent` - there is no terminal-failure/dead-letter status yet. A test,
  `NotificationOutboxStatus_HasNoTerminalFailureStatusYet_...`, asserts this directly so that whenever a
  future retry-processor task *does* add one, that test fails as a deliberate reminder to also exclude it
  here, rather than a due-item query silently returning dead-lettered rows forever.)
- `AvailableAtUtc <= now` - its scheduled availability instant (or retry backoff target - see
  "Notification outbox" above for why those are the same field) has already passed.

No other field decides "due" - in particular, `AttemptCount` is read-but-not-filtered-on (nothing here
limits attempts or dead-letters anything; that is explicitly next-task work).

### Why a bounded batch, and why not just load the whole table

A production outbox can accumulate a large backlog (thousands of reminders scheduled for the same
morning, say). Loading all of it into application memory on every poll would make memory usage scale with
backlog size, not with how much work the application can realistically act on per cycle - exactly the
failure mode a bounded batch exists to prevent. `GetDueBatchAsync(batchSize, ct)` always returns **at
most** `batchSize` items, and the limit is enforced by the database itself (a SQL `TOP`), not by fetching
more and truncating client-side.

### The query shape

```csharp
dbContext.NotificationOutboxItems
    .IgnoreQueryFilters()
    .AsNoTracking()
    .Where(item => item.Status == NotificationOutboxStatus.Pending && item.AvailableAtUtc <= DateTimeOffset.UtcNow)
    .OrderBy(item => item.AvailableAtUtc)
    .ThenBy(item => item.CreatedAtUtc)
    .ThenBy(item => item.Id)
    .Take(batchSize)
    .Select(item => new DueNotificationOutboxItem(/* ... */))
    .ToListAsync(cancellationToken);
```

`Where`, `OrderBy`/`ThenBy`, and `Take` all run in SQL, confirmed directly against the generated SQL
(`IQueryable.ToQueryString()`), not assumed from reading the LINQ:

```sql
SELECT TOP(@p) [n].[Id], [n].[TenantId], [n].[NotificationType], [n].[RecipientUserId],
               [n].[PayloadJson], [n].[AvailableAtUtc], [n].[CreatedAtUtc], [n].[AttemptCount]
FROM [NotificationOutboxItems] AS [n]
WHERE [n].[Status] = N'Pending' AND [n].[AvailableAtUtc] <= CAST(SYSUTCDATETIME() AS datetimeoffset)
ORDER BY [n].[AvailableAtUtc], [n].[CreatedAtUtc], [n].[Id]
```

Note what is **not** selected: `Status` and `IdempotencyKey` are used only in the `WHERE`/uniqueness
story, never returned - `DueNotificationOutboxItem` (the projection) carries only what a future processor
actually needs. `.AsNoTracking()` is technically redundant once the query ends in `.Select()` into a
non-entity record (EF never tracks a projected result regardless), but is kept anyway for
defense-in-depth clarity, matching the same choice in `NotificationOutboxWriter`.

### Clock source for the due-batch query

`DateTimeOffset.UtcNow` is written directly inline in the `Where` clause above - not captured into a
local variable first. This matters: EF Core's SQL Server provider recognizes `DateTimeOffset.UtcNow`
written this way as server-evaluable and translates it to `CAST(SYSUTCDATETIME() AS datetimeoffset)`,
confirmed in the generated SQL above - the comparison is evaluated **by the database**, using the
database's own clock, not this application instance's local clock. Several application instances share
this one query; anchoring "now" to the one clock they all query against (rather than each instance's own,
possibly skewed, local `DateTime.UtcNow`) means they never disagree about which items are currently due.
This is the same clock-authority principle the WP-8 lease lock established for lease expiry, now reached
through idiomatic LINQ rather than raw SQL - worth calling out since `NotificationOutboxWriter`
deliberately does *not* do this for its own `CreatedAtUtc`/`AvailableAtUtc` writes (see "Notification
outbox" above for why that asymmetry is intentional: a write recording "now" once isn't a multi-instance
agreement question the way repeatedly deciding "is this due yet" is).

### Deterministic ordering

`ORDER BY AvailableAtUtc, CreatedAtUtc, Id` - oldest-due-first, with two tie-breakers:

1. **`CreatedAtUtc`** - among items that became due at the exact same instant (realistic, not just
   theoretical: a future reminder job scheduling many bookings' reminders for one shared computed
   instant would produce exactly this), the one that was actually enqueued earlier is processed first - a
   more meaningful secondary order than falling straight to an arbitrary id comparison.
2. **`Id`** - the final, always-unique tie-breaker, for the (rarer still) case of two items sharing both
   `AvailableAtUtc` and `CreatedAtUtc`. Without it, two rows tied on every other column would have no
   guaranteed order at all - SQL Server does not promise any particular order for ties beyond what the
   `ORDER BY` clause actually specifies. A test proves this specific case is still deterministic by
   running the identical query twice against unchanged, fully-tied data and asserting both calls return
   the same sequence - not by asserting a specific absolute order, since SQL Server's `uniqueidentifier`
   sort order does not match .NET's `Guid.CompareTo`, and asserting against that mismatch would encode the
   wrong assumption.

### The index: empirically confirmed, not assumed

The WP-8 notification-outbox task already added a filtered index,
`IX_NotificationOutboxItems_AvailableAtUtc` (`WHERE [Status] = 'Pending'`), anticipating this exact query
shape. Checking it empirically (seeding a realistic LocalDB table - a majority of rows `Sent`, most
`Pending` rows scheduled in the future, a small handful actually due - and inspecting the real execution
plan) showed that index, *as it originally stood*, was **not actually used**: SQL Server chose a full
`Clustered Index Scan` over seeking it, because the index covered only `AvailableAtUtc` - satisfying the
`SELECT`'s other columns from a nonclustered index seek would have needed a key lookup per matching row,
and the optimizer judged a full scan cheaper than that.

This task's one migration, `AddNotificationOutboxDueIndexCoveringColumns`, adds `INCLUDE` columns to that
same index (`TenantId`, `NotificationType`, `RecipientUserId`, `PayloadJson`, `CreatedAtUtc`,
`AttemptCount` - exactly `DueNotificationOutboxItem`'s projected columns) so it becomes a **covering**
index for this query - nothing needed from the base table at all. Re-running the identical plan check
afterward showed SQL Server switch to an `Index Seek` on that index, `ORDERED FORWARD`, with roughly a 6x
lower estimated subtree cost than the scan. The migration touches only this one index (`DROP INDEX` +
`CREATE INDEX`, both against `NotificationOutboxItems`) - it does not edit the earlier, already-merged
`AddNotificationOutbox` migration, and touches no other table.

### `BatchSize` configuration

```json
"BackgroundJobs": {
  "BatchSize": 50
}
```

- **Default `50`** - callers (eventually `BackgroundJobsWorker`) read this from
  `IOptions<BackgroundJobsOptions>.Value.BatchSize` rather than any caller hardcoding a number.
- **`BackgroundJobsOptionsValidator`** rejects a non-positive value (same `ValidateOnStart()` mechanism
  every other `BackgroundJobs:*` setting already uses) and rejects a value above
  `BackgroundJobsOptions.MaxBatchSize` (`1000`) - generous for any realistic per-poll volume this system
  needs, while still guaranteeing a configuration typo (an extra stray zero, say) can never make a single
  poll try to pull an unbounded number of rows into memory. `GetDueBatchAsync` itself also rejects a
  non-positive `batchSize` argument directly (`ArgumentOutOfRangeException`), independent of whether the
  caller actually went through validated options.

### Tenant behavior

`GetDueBatchAsync` is a **system-wide background read**: it must see due items across every tenant in one
query, which is exactly why it calls `.IgnoreQueryFilters()` - the same narrow, justified exception
pattern `docs/tenant-isolation.md` already documents for `IUserRepository.FindByEmailAsync`, applied here
for the first time to a *different* reason (a background worker with no `ICurrentUserContext.TenantId` at
all, rather than a pre-authentication lookup). Each returned `DueNotificationOutboxItem` still carries its
own `TenantId`, so a future processor always knows which tenant's context to act in without a second
lookup per item. This bypass is scoped to exactly this one query in `NotificationOutboxReader` - it does
not relax the global filter for anything else. A dedicated test (`OrdinaryTenantScopedQuery_...`) proves
an ordinary, tenant-scoped query against the very same table is completely unaffected: it still only ever
sees its own tenant's rows.

### Why this is not wired into the worker yet

`NoOpBackgroundJobCycle` still does nothing. Wiring `GetDueBatchAsync` into the real cycle today would
mean fetching the same batch of due items on every poll with nothing yet acting on them, re-fetching an
identical batch forever - there is no email sender, no per-item success/retry/dead-letter handling, and
nothing here ever marks an item as sent or increments `AttemptCount`. The intended future call chain:

```text
BackgroundJobsWorker
  -> acquire the job lease (already built - see "Job lease lock" above)
  -> run the cycle
      -> GetDueBatchAsync(BatchSize)
      -> a future per-item processor: send, then mark Sent / bump AttemptCount and reschedule / dead-letter
```

That per-item processor, its retry/backoff computation, and wiring it into `IBackgroundJobCycle` are the
next task's work, not this one's.

## What the hosted-service foundation and lease lock branches did not implement

Reminder emails, no-show processing, stale-approval handling, an email provider, retry/backoff beyond
"log and continue to the next poll," an ICS feed, or any new booking business logic. All of that remains
future WP-8 work, layered on top of `IBackgroundJobCycle` without needing to change `BackgroundJobsWorker`,
`JobLeaseCoordinator`, or `JobLeaseStore` themselves. In particular: the lease lock is infrastructure for
*scheduling*, not for *correctness of business side effects* (see "What this guarantees, and what it does
not" above), and the notification outbox is infrastructure for *durable, deduplicated intent*, not for
*guaranteed email delivery* (see "What this task guarantees - and does not" above).
