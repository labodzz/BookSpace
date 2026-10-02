using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BookSpace.Infrastructure.Persistence;

// See docs/bookings-and-concurrency.md for the full reasoning behind this strategy. In short: SQL Server
// has no PostgreSQL-style exclusion constraint for overlapping ranges, so the SUM-over-overlapping-
// interval capacity invariant can't be enforced by a unique index the way other conflicts in this
// codebase are (e.g. duplicate resource names). Instead, this takes a transaction-scoped exclusive lock
// on the parent Resource row via SQL Server's UPDLOCK+HOLDLOCK table hints - a smaller, more precise
// lock footprint than a SERIALIZABLE range scan over Bookings would take (which would lock that
// resource's entire booking history up to the new booking's end time), and it needs no app-level retry
// loop for the ordinary contention case: a second caller simply blocks until the first commits, then
// re-reads current state under its own lock.
internal sealed class ResourceBookingLock(BookSpaceDbContext dbContext, ILogger<ResourceBookingLock> logger) : IResourceBookingLock
{
    public async Task<TResult> RunExclusiveAsync<TResult>(
        Guid resourceId, Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsSqlServer())
        {
            // SQLite (fast, non-concurrent tests only) has no table-hint support and is never the
            // correctness authority for this invariant - see BookingAvailabilityRepository for the same
            // provider branch on the read side.
            return await operation(cancellationToken);
        }

        // AddInfrastructure configures this DbContext with EnableRetryOnFailure, which puts a retrying
        // execution strategy in front of every operation. That strategy refuses to let a caller open its
        // own transaction directly (BeginTransactionAsync) - it cannot safely retry only part of an
        // already-open transaction, and throws InvalidOperationException ("does not support user-initiated
        // transactions") the moment any query runs inside one. CreateExecutionStrategy().ExecuteAsync(...)
        // is EF Core's own prescribed fix: the whole delegate below - opening a fresh transaction,
        // re-acquiring the row lock, re-running `operation` - is what gets retried as one atomic unit if a
        // genuinely transient infrastructure fault (a dropped connection, a transient Azure SQL error)
        // occurs, rather than EF attempting to retry a single statement mid-transaction.
        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                // Locks exactly this one row until the transaction commits or rolls back (HOLDLOCK), so a
                // second concurrent call for the SAME resource blocks here until the first is fully done.
                // Executed as ExecuteNonQuery-style: the SELECT's result set is discarded, but SQL Server
                // still takes and holds the row lock the hints request.
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT TOP (1) Id FROM Resources WITH (UPDLOCK, HOLDLOCK) WHERE Id = {resourceId}", cancellationToken);

                var result = await operation(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch (SqlException exception) when (exception.Number is 1205 or 1222)
            {
                // 1205 = deadlock victim, 1222 = lock request timeout - both are ordinary application-level
                // lock contention, not an infrastructure fault the execution strategy itself would retry
                // (they are not in EF's default transient-error list), so this catch still runs exactly as
                // before: translated into a conflict for the caller, not silently retried.
                logger.LogWarning(
                    exception, "Booking-creation lock wait for resource {ResourceId} ended in a deadlock/timeout, handled as a conflict", resourceId);
                await transaction.RollbackAsync(cancellationToken);
                throw new ConflictException("The request conflicts with another in-progress booking for this resource. Please retry.");
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        });
    }
}
