# Optimistic Concurrency

## Which entities use it, and why

| Entity | Why | Doc |
|---|---|---|
| `RefreshToken` | Two concurrent refresh requests presenting the same still-valid token could otherwise both succeed, minting two live sibling tokens from one parent. | [authentication.md](authentication.md) |
| `Resource` | Two near-simultaneous `UpdateResource` requests loaded from the same starting state could silently overwrite each other's changes with no signal to either caller. | [resource-lifecycle-and-capacity.md](resource-lifecycle-and-capacity.md) |
| `BlackoutPeriod` | Same last-write-wins risk as `Resource`, for the same kind of concurrent-update scenario. | [resource-lifecycle-and-capacity.md](resource-lifecycle-and-capacity.md) |

**No other entity in the codebase has a `RowVersion` column.** Do not assume optimistic concurrency
protection exists anywhere it isn't explicitly listed above.

## Mechanism

Each entity above has a `byte[] RowVersion { get; set; } = [];` property, mapped via
`entity.Property(x => x.RowVersion).IsRowVersion()` in `BookSpaceModelConfiguration.cs` - a SQL Server
`rowversion`/`timestamp` column, server-generated and updated automatically on every write, never set
by application code.

**The mapping is provider-conditional**: `BookSpaceDbContext.OnModelCreating` passes
`useRowVersionColumns: Database.IsSqlServer()` into `ConfigureBookSpaceModel`, and each `RowVersion`
property is only configured as `IsRowVersion()` when that's `true`. SQLite (used by the
`BookSpace.Api.Tests` integration suite for speed) has no equivalent server-generated column type -
mapping it the same way there produces a `NOT NULL` constraint violation on every single insert, since
nothing ever populates the column. On SQLite, `RowVersion` is just an ordinary, always-empty `byte[]`
property with no concurrency semantics at all; **real concurrency-conflict behavior for these three
entities can only be proven against actual SQL Server**, which is exactly why each has a dedicated
LocalDB-backed test class (`RefreshTokenRotationConcurrencyTests`, `OptimisticConcurrencyTests`) rather
than relying on the SQLite-backed integration suite.

## Stale-update and conflict behavior

`DbContextConcurrencyExtensions.SaveChangesHandlingConflictsAsync` - the same extension method that
already translated SQL unique-constraint violations (error 2601/2627) into `ConflictException` - now
also catches `DbUpdateConcurrencyException` (a `RowVersion` mismatch) and translates it the same way.
Every repository's own `SaveChangesAsync` implementation routes through this helper, so a handler never
needs to know which failure mode it's looking at - it just sees `ConflictException`, which the existing
`ConflictExceptionHandler` maps to a `409` `ProblemDetails` response exactly like any other conflict.
**No raw EF exception is ever allowed to reach a client.**

A stale write - one whose `RowVersion` no longer matches what's currently stored, because another
request committed a change in between - fails with `409`, full stop. It never silently overwrites the
newer value; the failing request's caller must reload and retry.

## No API/DTO contract change

None of the three response DTOs (`UpdateResourceResponse`, `UpdateBlackoutPeriodResponse`,
`AuthTokens`) expose a `RowVersion`/`ETag`-style field, and no request DTO accepts one as an input.
This is possible because every Update handler loads, mutates, and saves the entity **within the same
request** - EF Core's change tracker already knows the `RowVersion` value that request's own read saw,
and includes it in the `WHERE` clause of the resulting `UPDATE` automatically. A client never needs to
round-trip a concurrency token for this to work.

## Guidance for adding a new mutable aggregate

When adding a new entity with an Update path, decide explicitly whether last-write-wins is acceptable
for it:

- If two concurrent updates silently overwriting each other would be a real problem (data loss,
  inconsistent state, a business invariant depending on "what was true when I read it"), add a
  `RowVersion` following the exact pattern above: domain property, provider-conditional
  `IsRowVersion()` mapping, route the repository's `SaveChangesAsync` through
  `SaveChangesHandlingConflictsAsync`, and add a dedicated real-SQL-Server test proving the conflict
  actually surfaces as `409`, not just that the mapping compiles.
- If last-write-wins is genuinely fine (e.g. the field is a display-only preference with no invariant
  riding on it), don't add one speculatively. **Do not add `RowVersion` to every entity "just in case"**
  - it's a real schema change and a real test-writing cost, and most entities in this codebase don't
    need it.
