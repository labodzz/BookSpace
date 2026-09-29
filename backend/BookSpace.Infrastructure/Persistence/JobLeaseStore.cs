using BookSpace.Application.BackgroundJobs;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

// SQL Server implementation of the job lease primitives - see docs/background-jobs.md ("Job lease
// lock") for the full protocol. Every mutation is one parameterized, conditional UPDATE/INSERT/DELETE
// statement (never a separate read-then-write), and every expiry comparison uses SYSUTCDATETIME() - the
// database server's own UTC clock - rather than this instance's local DateTime.UtcNow, so two instances
// with skewed local clocks can never disagree about whether a lease has expired.
//
// TryAcquireAsync's UPDATE-then-INSERT shape does not need a RowVersion/concurrency token: the UPDATE's
// WHERE clause is itself the atomic compare-and-swap (SQL Server evaluates the predicate and applies the
// change as one atomic operation per row), and the INSERT's fallback is protected by JobName being the
// table's primary key - the "unique constraint as the last physical safety net" the acquire protocol
// relies on when two instances' INSERTs race for the same still-missing row.
internal sealed class JobLeaseStore(BookSpaceDbContext dbContext) : IJobLeaseStore
{
    // Mirrors DbContextConcurrencyExtensions's constants - duplicated locally because raw
    // ExecuteSqlInterpolatedAsync calls surface a plain SqlException, never the DbUpdateException that
    // wrapping applies only to SaveChangesAsync.
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;

    public async Task<DateTimeOffset?> TryAcquireAsync(string jobName, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var leaseDurationSeconds = (int)leaseDuration.TotalSeconds;

        // Steals an expired lease, or safely re-acquires this same owner's still-valid one - either way,
        // a single conditional statement so two concurrent callers can never both see "free" and both
        // proceed.
        var updated = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE JobLeases
            SET OwnerId = {ownerId},
                LeaseExpiresAtUtc = DATEADD(SECOND, {leaseDurationSeconds}, SYSUTCDATETIME()),
                LastHeartbeatAtUtc = SYSUTCDATETIME()
            WHERE JobName = {jobName} AND (OwnerId = {ownerId} OR LeaseExpiresAtUtc <= SYSUTCDATETIME());
            """,
            cancellationToken);

        if (updated > 0)
        {
            return await ReadExpiryAsync(jobName, cancellationToken);
        }

        try
        {
            // Reached only when no row exists yet for this job (the UPDATE above matched nothing). The
            // WHERE NOT EXISTS guard narrows the race but cannot close it by itself - two callers can both
            // pass it before either commits, so the primary key on JobName is the actual backstop; the
            // catch below turns that expected constraint violation into an ordinary "lost the race" result.
            var inserted = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO JobLeases (JobName, OwnerId, LeaseExpiresAtUtc, LastHeartbeatAtUtc)
                SELECT {jobName}, {ownerId}, DATEADD(SECOND, {leaseDurationSeconds}, SYSUTCDATETIME()), SYSUTCDATETIME()
                WHERE NOT EXISTS (SELECT 1 FROM JobLeases WHERE JobName = {jobName});
                """,
                cancellationToken);

            return inserted > 0 ? await ReadExpiryAsync(jobName, cancellationToken) : null;
        }
        catch (SqlException sqlException) when (sqlException.Number is UniqueIndexViolation or UniqueConstraintViolation)
        {
            // Another instance's INSERT (or, in principle, UPDATE) won the race between our own WHERE NOT
            // EXISTS check and this INSERT - an expected outcome of concurrent acquisition, not an error.
            return null;
        }
    }

    public async Task<DateTimeOffset?> TryRenewAsync(string jobName, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var leaseDurationSeconds = (int)leaseDuration.TotalSeconds;

        // Only renews while OwnerId still matches AND the lease has not already expired - a stale owner
        // (expired and possibly already taken over by someone else) can never resurrect its old lease
        // through this path.
        var updated = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE JobLeases
            SET LeaseExpiresAtUtc = DATEADD(SECOND, {leaseDurationSeconds}, SYSUTCDATETIME()),
                LastHeartbeatAtUtc = SYSUTCDATETIME()
            WHERE JobName = {jobName} AND OwnerId = {ownerId} AND LeaseExpiresAtUtc > SYSUTCDATETIME();
            """,
            cancellationToken);

        return updated > 0 ? await ReadExpiryAsync(jobName, cancellationToken) : null;
    }

    public async Task<bool> TryReleaseAsync(string jobName, string ownerId, CancellationToken cancellationToken)
    {
        // OwnerId-conditioned so one instance can never delete (or otherwise disturb) a lease row another
        // instance now legitimately owns.
        var deleted = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM JobLeases WHERE JobName = {jobName} AND OwnerId = {ownerId};",
            cancellationToken);

        return deleted > 0;
    }

    private async Task<DateTimeOffset?> ReadExpiryAsync(string jobName, CancellationToken cancellationToken) =>
        await dbContext.JobLeases
            .AsNoTracking()
            .Where(lease => lease.JobName == jobName)
            .Select(lease => (DateTimeOffset?)lease.LeaseExpiresAtUtc)
            .SingleOrDefaultAsync(cancellationToken);
}
