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
