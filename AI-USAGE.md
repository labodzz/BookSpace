# AI usage log

Transparency log for AI-assisted work on BookSpace. One entry per work package (or per significant
session). Fill in as you go — don't backfill from memory at the end.

## How to log an entry

```
## <date> — <work package / topic>

**Tool:** <e.g. Claude Code>
**What I asked for:** <short description of the prompt/task>
**What the AI produced:** <files/areas touched, at a glance>
**What I changed or rejected:** <anything you edited, reverted, or asked to redo, and why>
**What I understand and could explain without notes:** <the part you'd want checked in a review>
```

## Log

<!-- Add entries below, most recent first. -->

## 2026-08-27 — Fix: Correlation ID Missing from Console Log Lines

**Tool:** Claude Code
**What I asked for:** Wanted the correlation ID to actually show up in the terminal next to each log
line while running the app locally and testing a route in Postman - pointed out that's the whole
point of correlation-ID logging, not something to leave for later.
**What the AI produced:** Added an explicit console `outputTemplate` (`... (cid: {CorrelationId}) ...`)
to the fully configured Serilog logger. Ran the app and hit it with curl to check, and found the
per-request summary line from `UseSerilogRequestLogging()` (`HTTP GET /health/db responded 200 in
...ms` - the single most useful line for following a request) still had the old default format with
no correlation ID at all, while the mediator's own "Handling X" lines showed it correctly. Root cause:
`UseSerilogRequestLogging()` logs through the static `Serilog.Log.Logger`, not the DI-registered
logger from `builder.Host.UseSerilog(...)` - a distinction that only exists because of the earlier
`preserveStaticLogger: true` fix for the WebApplicationFactory test-host issue. Fixed by extracting
the template into a shared constant and applying it to both the bootstrap logger at the top of
Program.cs and the fully configured one, so every console line uses the same format regardless of
which of the two loggers actually writes it.
**What I changed or rejected:** Didn't stop at "the correlation ID shows up in the first log line I
checked" - ran a login-validation-failure request too, specifically to see the request-summary line,
because that failure mode (individual log statements correct, the one summary line silently wrong)
would have shipped invisibly with an incomplete test.
**What I understand and could explain without notes:** Why there are two Serilog loggers in this app
at all (the static bootstrap one for pre-DI startup failures, and the DI-configured one everything
else uses) - and why `preserveStaticLogger: true`, which fixed a different problem earlier, is
exactly what makes `UseSerilogRequestLogging()`'s default logger choice diverge from the rest of the
app's, since without it the two loggers would already be the same object.
## 2026-08-27 — Fix: Correlation ID Missing from Error Response Headers

**Tool:** Claude Code
**What I asked for:** After the WP-2 logging/exception-handling work was already merged, asked
where to actually see the logs while testing a route in Postman - which led to running the app for
real and checking the response headers by hand rather than just trusting the earlier unit tests.
**What the AI produced:** Ran the API locally and hit it with curl: `X-Correlation-Id` was present
on a normal 200/401 response but silent on a 400 from `ValidationExceptionHandler` - the
correlation ID only showed up in the JSON body's `correlationId` field, not the header.
`UseExceptionHandler` resets the response (including headers) before an `IExceptionHandler` runs, so
`CorrelationIdMiddleware`'s early header write doesn't survive an exception. Fixed by setting the
header again inside `ValidationExceptionHandler` and `GlobalExceptionHandler` themselves, alongside
the `correlationId` extension they already set; added header assertions to the existing unit and
integration tests so this can't regress silently.
**What I changed or rejected:** This was found by manually running the app and reading real HTTP
responses, not by re-reading the code - the existing unit tests for both handlers only checked the
`ProblemDetails` body, never the response header, so they were green the whole time this was broken.
**What I understand and could explain without notes:** Why the header genuinely needs to be set from
inside each `IExceptionHandler` rather than trying to make the middleware's original header
"survive" some other way - the exception-handling reset is by design (it stops a partially-built
failed response from leaking headers/content-type into the client-visible error), so anything that
needs to be on the final response after an exception has to be (re)written after that reset happens,
not before it.

## 2026-08-27 — WP-2: Global Exception Handling

**Tool:** Claude Code
**What I asked for:** A global exception handler (middleware or `IExceptionHandler`) so no
unhandled exception ever leaks a stack trace to the client; domain/validation errors mapped to
clean, consistent `ProblemDetails` responses with the correlation ID (from the structured-logging
entry below) attached; and the full exception logged server-side exactly once, at the boundary,
not via scattered try/catch that swallows or double-logs.
**What the AI produced:** `GlobalExceptionHandler` (`IExceptionHandler`), registered after the
existing `ValidationExceptionHandler` so it only runs as the catch-all fallback (`IExceptionHandler`s
try in registration order, first `true` wins). It logs the full exception with `ILogger.LogError`
once, then writes a generic `ProblemDetails` 500 that never includes the exception's message or
stack trace - only a title and the correlation ID pulled from `ICorrelationIdContext`.
`ValidationExceptionHandler` got the same correlation ID attached to its `ValidationProblemDetails`,
plus an `Information`-level structured log line (which fields failed and how many, never the
submitted values - a validation failure is expected user error, not a bug worth an Error-level log).
Grepped the whole backend for `catch` first: the only one was the Serilog bootstrap's own top-level
try/catch from the structured-logging work, confirming there was nothing scattered to consolidate -
exceptions already propagated cleanly to this one boundary.
**What I changed or rejected:** My first integration test for `GlobalExceptionHandler` swapped in a
throwing `ICurrentUserContext` and used it to hit `/users/me` - but it broke `/auth/login` too,
because `BookSpaceDbContext`'s global tenant query filter reads `ICurrentUserContext.TenantId` on
every query against a tenant-owned table, including the one login runs to look up a user by email.
Fixed by having only `UserId` throw in the test double and keeping `TenantId` harmless, rather than
loosening the assertion or dropping the test.
**What I understand and could explain without notes:** Why exception-handler registration order
matters here - `ValidationExceptionHandler` has to run before `GlobalExceptionHandler` so its more
specific 400 response wins over the generic 500 fallback - and why the generic handler's response
body is checked against the exception's own message and type name in tests, not just its status
code: a stack trace can leak through a field that isn't `Detail` (an `Extensions` entry, a `Title`
built from `exception.Message`) just as easily as through the obvious one.

## 2026-08-27 — WP-2: Structured Logging (Serilog)

**Tool:** Claude Code
**What I asked for:** Configure Serilog as the logging provider (replacing the default one), log
structured key/value properties rather than interpolated strings, attach a correlation ID to every
request and flow it through all logs for that request (with an eye toward background jobs later),
and set sensible sinks/levels - console in dev, a structured queryable sink otherwise - while never
logging secrets, tokens, or passwords.
**What the AI produced:** A two-stage Serilog bootstrap in `Program.cs` (`Log.Logger` as a minimal
startup logger, then `builder.Host.UseSerilog(...)` for the fully configured one) with `UseSerilogRequestLogging()`
for a per-request summary line; console sink always, plus a compact-JSON rolling file sink for
every environment except Development and the test suite's `Testing` environment.
`ICorrelationIdContext`/`CorrelationIdContext` (`BookSpace.Application/Logging/`) hold the current
correlation ID on an `AsyncLocal`, not `HttpContext.Items`, specifically so the same mechanism can
carry a correlation ID through a background job later with no HTTP request involved.
`CorrelationIdMiddleware` reads `X-Correlation-Id` from the incoming request (or mints one), sets it
on that context, pushes it onto Serilog's `LogContext` so every log for the rest of the request
carries it automatically, and echoes it back on the response header.
**What I changed or rejected:** Hit "the logger is already frozen" from every integration test as
soon as the two-stage bootstrap went in - `WebApplicationFactory<Program>` builds this host more
than once per process, which collides with Serilog's default single-use `ReloadableLogger`. Fixed
by passing `preserveStaticLogger: true` to `UseSerilog` rather than working around it with test-only
configuration, so the production code path stays correct and the tests exercise the real thing.
**What I understand and could explain without notes:** Why the file sink is gated on environment
name rather than always-on - writing it during `dotnet test` runs would leave log files behind on
every run for a sink nobody's querying - and why the correlation ID has to be pushed onto Serilog's
`LogContext` rather than passed explicitly to each log call: `LogContext` is itself `AsyncLocal`-backed,
so once it's pushed at the top of the request every subsequent `ILogger` call for that request -
including from deep inside the mediator pipeline - picks it up automatically without threading it
through method signatures.

## 2026-08-27 — WP-3: Custom Mediator, Pipeline Behaviors & Validation

**Tool:** Claude Code
**What I asked for:** A hand-rolled mediator (explicitly not MediatR) mapping requests to
handlers, with controllers reduced to thin `IMediator.Send(...)` calls and the Application
layer holding the logic; a pipeline supporting cross-cutting behaviors (logging, validation);
FluentValidation wired as a pipeline behavior running before the handler; and a validation
failure short-circuiting to a clean 400 with field errors via the global exception handler,
never a raw exception.
**What the AI produced:** `BookSpace.Application/Mediator/` (`IRequest`, `IRequestHandler`,
`IPipelineBehavior`, `IMediator`, `DefaultMediator` with a cached per-request-type dispatch
wrapper, `LoggingBehavior`, `ValidationBehavior`); `LoginCommand`/`RefreshCommand` and
`GetCurrentUserQuery`/`GetUsersQuery` with their handlers, replacing the logic that used to
live directly in `AuthController`/`UsersController` (including `UsersController` reading
`BookSpaceDbContext` directly); FluentValidation validators for both commands, auto-registered
via assembly scan; `ValidationExceptionHandler` (`IExceptionHandler`) mapping
`FluentValidation.ValidationException` to a `ValidationProblemDetails` 400; a new
`IUserRepository.GetAllAsync` so the Users query handler stays in the Application layer instead
of reaching into Infrastructure; unit tests for the mediator dispatch/pipeline ordering, both
behaviors, both validators, and every handler in isolation; integration tests asserting the
400/field-error contract end-to-end.
**What I changed or rejected:** Had it walk back through every touched service/interface
(`IAuthenticationService`, `ICurrentUserContext`, `IUserRepository`) after the fact to confirm
nothing was left half-wired or duplicated, and separately asked for the test coverage gaps
(the logging behavior, the thin command/query handlers, the exception handler) to be filled in
rather than accepting integration coverage alone as sufficient.
**What I understand and could explain without notes:** Why `DefaultMediator` needs a
reflection-based wrapper at all - `Send<TResponse>(IRequest<TResponse>)` only knows the
concrete request type at runtime, so a generic `IRequestHandler<TRequest,TResponse>` can't be
resolved from DI without it - and why that reflection cost is paid once per request type
(cached) rather than per call; and why pipeline behavior registration order is execution order,
with the first-registered behavior outermost.

## 2026-08-20 — WP-0: Project Setup & Foundations

**Tool:** Claude Code
**What I asked for:** Trim the repo back to exactly what WP-0 (and no more) requires, reset the git
history that had drifted ahead of scope, then do WP-0 properly: repo/branch setup, README, separate
backend/frontend scaffolds, DB connectivity, .editorconfig, this log.
**What the AI produced:** Reset `dev` and `master` to the bare pre-scaffold commit; split the project into
`backend/BookSpace.Api` (.NET Web API) and `frontend/` (Angular, `ng new`); added README.md,
.editorconfig, Node/Angular `.gitignore` entries, and a `/health/db` endpoint confirming SQL Server
LocalDB connectivity; did the work on a `feature/wp0-project-setup` branch merged into `dev` via PR,
then deleted.
**What I changed or rejected:** Paused it before pushing the branch to verify task-by-task against the
WP-0 document myself, rather than trusting the "done" summary at face value; also had it explicitly
confirm before any force-push to `master`.
**What I understand and could explain without notes:** Why the git history needed a hard reset (an
already-merged PR had carried the old over-scope commit back into `master` independently of `dev`), and
why DB connectivity is proven with a real endpoint call, not just an assumption.
