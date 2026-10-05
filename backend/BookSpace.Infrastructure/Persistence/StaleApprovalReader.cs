using System.Diagnostics;
using BookSpace.Application.Notifications;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BookSpace.Infrastructure.Persistence;

// Read-only due-stale-approval query - see docs/background-jobs.md ("Stale-approval job") for the full
// design, including why IgnoreQueryFilters() here is a narrow, justified exception to tenant isolation (a
// system-wide background read, not a web request, the same justification as NotificationOutboxReader/
// BookingReminderReader), and how the generated SQL and index were verified against real SQL
// Server/LocalDB rather than assumed.
internal sealed class StaleApprovalReader(BookSpaceDbContext dbContext, ILogger<StaleApprovalReader> logger) : IStaleApprovalReader
{
    public async Task<IReadOnlyList<DueStaleApproval>> GetDueApprovalsAsync(int batchSize, CancellationToken cancellationToken)
    {
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "batchSize must be a positive number of items.");
        }

        var stopwatch = Stopwatch.StartNew();

        // IgnoreQueryFilters() on both sides of the join: the one, narrow infrastructure exception to the
        // global tenant query filter for this query - see docs/tenant-isolation.md. A system-wide
        // background read that must see every tenant's stale approvals in one query has no HTTP
        // request/JWT claim to scope it by.
        //
        // DateTimeOffset.UtcNow inline, never captured into a local variable first - same reasoning as
        // NotificationOutboxReader/BookingReminderReader: EF Core's SQL Server provider translates this
        // into SYSUTCDATETIME() evaluated BY THE DATABASE at execution time, never this application
        // instance's own clock - several instances must all agree on exactly which approvals are already
        // past their own ExpiresAtUtc.
        //
        // No ApprovalRequest navigation to Booking exists on the entity (BookingId is a plain scalar FK) -
        // this Join is the SQL-translatable equivalent, matched on Booking.Id (the clustered PK), which is
        // why this stays efficient even though it is not a single-table query: the ApprovalRequests side
        // narrows to a small due set via its own filtered index first, and each match is then a trivial PK
        // lookup into Bookings, not a scan.
        var items = await dbContext.ApprovalRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(approvalRequest => approvalRequest.Status == ApprovalStatus.Pending && approvalRequest.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            .Join(
                dbContext.Bookings.IgnoreQueryFilters().AsNoTracking().Where(booking => booking.Status == BookingStatus.Pending),
                approvalRequest => approvalRequest.BookingId,
                booking => booking.Id,
                (approvalRequest, booking) => new { approvalRequest, booking })
            // Oldest-requested-first, with Id as a stable tie-breaker - ApprovalRequest has no CreatedAtUtc
            // column (RequestedAtUtc fills that role here), but the same realistic-tie reasoning as every
            // other due query in this codebase still applies.
            .OrderBy(joined => joined.approvalRequest.RequestedAtUtc)
            .ThenBy(joined => joined.approvalRequest.Id)
            .Take(batchSize)
            .Select(joined => new DueStaleApproval(
                joined.approvalRequest.Id,
                joined.booking.Id,
                joined.approvalRequest.TenantId,
                joined.booking.ResourceId,
                joined.booking.UserId,
                joined.booking.StartUtc,
                joined.booking.EndUtc,
                joined.booking.Quantity))
            .ToListAsync(cancellationToken);

        // One summary line per call, never one per row - and never any recipient identifier, only counts
        // and timing.
        logger.LogInformation(
            "Stale-approval due query returned {ReturnedCount} of up to {RequestedBatchSize} requested candidates in {ElapsedMilliseconds}ms.",
            items.Count,
            batchSize,
            stopwatch.ElapsedMilliseconds);

        return items;
    }
}
