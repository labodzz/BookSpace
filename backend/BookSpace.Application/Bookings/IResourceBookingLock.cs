namespace BookSpace.Application.Bookings;

// The concurrency boundary for booking creation - see docs/bookings-and-concurrency.md for the full
// reasoning. Acquires an exclusive, transaction-scoped lock on the given resource before running
// `operation`, so two concurrent CreateBookingCommandHandler calls for the SAME resource can never both
// pass their capacity re-check and insert: the second blocks until the first's transaction commits or
// rolls back, then re-reads current state under its own lock. `operation`'s own repository calls must
// resolve to the same scoped DbContext this lock uses (true by default under normal per-request DI
// scoping) so they run inside the same transaction the lock opened. The transaction commits only if
// `operation` completes without throwing; any exception rolls back, leaving no partial state, and
// releases the lock.
//
// On a provider without table-hint support (SQLite, used only for fast, non-concurrent tests), this runs
// `operation` with no locking guarantee - acceptable there because SQLite is never the correctness
// authority for this invariant; the real guarantee is proven only against SQL Server/LocalDB.
public interface IResourceBookingLock
{
    Task<TResult> RunExclusiveAsync<TResult>(
        Guid resourceId, Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken);
}
