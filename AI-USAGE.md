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
