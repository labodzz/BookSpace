using System.Diagnostics;
using BookSpace.Application.Notifications;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BookSpace.Infrastructure.Persistence;

// Read-only due-no-show query - see docs/background-jobs.md ("No-show job") for the full design, including
// why IgnoreQueryFilters() here is a narrow, justified exception to tenant isolation (a system-wide
// background read, not a web request, the same justification as NotificationOutboxReader/
// BookingReminderReader/StaleApprovalReader), and how the generated SQL and index were verified against
// real SQL Server/LocalDB rather than assumed.
internal sealed class NoShowReader(BookSpaceDbContext dbContext, ILogger<NoShowReader> logger) : INoShowReader
{
    public async Task<IReadOnlyList<DueNoShowBooking>> GetDueNoShowsAsync(int batchSize, CancellationToken cancellationToken)
    {
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "batchSize must be a positive number of items.");
        }

        var stopwatch = Stopwatch.StartNew();

        // IgnoreQueryFilters() only on the Bookings side - the one, narrow infrastructure exception to the
        // global tenant query filter for this query (see docs/tenant-isolation.md). Tenants carries no
        // TenantId of its own (it IS the tenant), so no filter exists there to bypass in the first place -
        // every other reader in this codebase that joins Tenant data (none did, before this) would find the
        // same thing.
        //
        // DateTimeOffset.UtcNow inline, never captured into a local variable first - same reasoning as
        // NotificationOutboxReader/BookingReminderReader/StaleApprovalReader: EF Core's SQL Server provider
        // translates this into SYSUTCDATETIME() evaluated BY THE DATABASE at execution time, never this
        // application instance's own clock - several instances must all agree on exactly which bookings are
        // already past their own deadline. The deadline itself (StartUtc + that booking's own tenant's
        // NoShowGraceMinutes) is computed in the SAME query via the join, translated to a DATEADD expression
        // evaluated by the database - never fetched first and compared in application code.
        //
        // Strictly "<" (not "<="): deadline must be STRICTLY in the past before a booking becomes a no-show
        // candidate - at the exact deadline instant, check-in is still allowed (CheckInEligibilityChecker's
        // own boundary is nowUtc <= deadline, inclusive), so no-show must never be able to win that exact
        // same instant. See docs/bookings-and-concurrency.md ("No-show processing - boundary alignment with
        // check-in") for the full before/after comparison.
        var items = await dbContext.Bookings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(booking => booking.Status == BookingStatus.Confirmed && booking.CheckedInAtUtc == null)
            .Join(
                dbContext.Tenants.AsNoTracking(),
                booking => booking.TenantId,
                tenant => tenant.Id,
                (booking, tenant) => new { booking, tenant })
            .Where(joined => joined.booking.StartUtc.AddMinutes(joined.tenant.NoShowGraceMinutes) < DateTimeOffset.UtcNow)
            // Earliest-overdue-first (by the per-tenant deadline itself, not raw StartUtc - two tenants with
            // different NoShowGraceMinutes values can disagree on which of two similarly-timed bookings
            // became due first), with CreatedAtUtc then Id as stable tie-breakers so a backlog larger than
            // one batch is worked through in the same order across repeated polls.
            .OrderBy(joined => joined.booking.StartUtc.AddMinutes(joined.tenant.NoShowGraceMinutes))
            .ThenBy(joined => joined.booking.CreatedAtUtc)
            .ThenBy(joined => joined.booking.Id)
            .Take(batchSize)
            .Select(joined => new DueNoShowBooking(
                joined.booking.Id,
                joined.booking.TenantId,
                joined.booking.ResourceId,
                joined.booking.UserId,
                joined.booking.StartUtc,
                joined.booking.EndUtc,
                joined.booking.Quantity))
            .ToListAsync(cancellationToken);

        // One summary line per call, never one per row - and never any recipient identifier, only counts
        // and timing.
        logger.LogInformation(
            "No-show due query returned {ReturnedCount} of up to {RequestedBatchSize} requested candidates in {ElapsedMilliseconds}ms.",
            items.Count,
            batchSize,
            stopwatch.ElapsedMilliseconds);

        return items;
    }
}
