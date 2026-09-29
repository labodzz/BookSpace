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

## What this branch does not implement

Reminder emails, no-show processing, stale-approval handling, an email provider, retry/backoff beyond
"log and continue to the next poll," idempotency/outbox tables, an ICS feed, or any new booking business
logic. All of that is future WP-8 work, layered on top of `IBackgroundJobCycle` without needing to change
`BackgroundJobsWorker`, `JobLeaseCoordinator`, or `JobLeaseStore` themselves. In particular: the lease
lock is infrastructure for *scheduling*, not for *correctness of business side effects* - see "What this
guarantees, and what it does not" above.
