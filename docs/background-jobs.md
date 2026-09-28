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

## What this branch does not implement

Reminder emails, no-show processing, stale-approval handling, an email provider, retry/backoff beyond
"log and continue to the next poll," a distributed lock/lease table, a heartbeat, idempotency/outbox
tables, an ICS feed, or any new booking business logic. All of that is future WP-8 work, layered on top
of `IBackgroundJobCycle` without needing to change `BackgroundJobsWorker` itself.
