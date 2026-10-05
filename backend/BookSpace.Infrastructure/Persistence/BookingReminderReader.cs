using System.Diagnostics;
using BookSpace.Application.Notifications;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BookSpace.Infrastructure.Persistence;

// Read-only due-reminder-candidate query - see docs/background-jobs.md ("Reminder job") for the full
// design, including why IgnoreQueryFilters() here is a narrow, justified exception to tenant isolation
// (a system-wide background read, not a web request, the same justification as NotificationOutboxReader),
// and how the generated SQL and index were verified against real SQL Server/LocalDB rather than assumed.
internal sealed class BookingReminderReader(BookSpaceDbContext dbContext, ILogger<BookingReminderReader> logger) : IBookingReminderReader
{
    public async Task<IReadOnlyList<DueBookingReminder>> GetDueBookingsAsync(TimeSpan leadTime, int batchSize, CancellationToken cancellationToken)
    {
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "batchSize must be a positive number of items.");
        }

        if (leadTime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leadTime), leadTime, "leadTime must be a positive duration.");
        }

        var stopwatch = Stopwatch.StartNew();
        var leadTimeMinutes = leadTime.TotalMinutes;

        // IgnoreQueryFilters(): the one, narrow infrastructure exception to the global tenant query filter
        // for this query - see docs/tenant-isolation.md. A system-wide background read that must see every
        // tenant's due reminders in one query has no HTTP request/JWT claim to scope it by.
        //
        // DateTimeOffset.UtcNow appears twice, inline, never captured into a local variable first - exactly
        // the same reasoning NotificationOutboxReader documents: EF Core's SQL Server provider translates
        // both the "already past" lower bound AND, via .AddMinutes(leadTimeMinutes), the "not too far in
        // the future" upper bound into SYSUTCDATETIME()/DATEADD(minute, @leadTimeMinutes, SYSUTCDATETIME())
        // evaluated BY THE DATABASE at execution time - never this application instance's own clock. That
        // matters here even more than for the notification due-batch query: several instances, each
        // possibly with a slightly different system clock, must all agree on exactly which bookings are
        // "upcoming," or two instances could disagree about whether a given booking's reminder is due yet.
        // See docs/background-jobs.md ("Reminder job - database clock") for how this was confirmed against
        // the actual generated SQL, not assumed.
        //
        // No pre-filter against NotificationOutboxItems for a reminder already enqueued - see
        // BookSpace.Application.BackgroundJobs.ReminderProducer's own doc comment for why that exclusion is
        // deliberately left to EnqueueManyAsync's database constraint instead of attempted here.
        var items = await dbContext.Bookings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(booking => booking.Status == BookingStatus.Confirmed
                && booking.StartUtc > DateTimeOffset.UtcNow
                && booking.StartUtc <= DateTimeOffset.UtcNow.AddMinutes(leadTimeMinutes))
            // Oldest-starting-first, with CreatedAtUtc then Id as stable tie-breakers - same reasoning as
            // NotificationOutboxReader's own ordering: two bookings can realistically share the exact same
            // StartUtc (two different resources booked for the same meeting time), so a deterministic
            // final order still matters for making stable progress through a backlog larger than one batch.
            .OrderBy(booking => booking.StartUtc)
            .ThenBy(booking => booking.CreatedAtUtc)
            .ThenBy(booking => booking.Id)
            .Take(batchSize)
            .Select(booking => new DueBookingReminder(
                booking.Id,
                booking.TenantId,
                booking.UserId,
                booking.ResourceId,
                booking.StartUtc,
                booking.EndUtc,
                booking.Quantity))
            .ToListAsync(cancellationToken);

        // One summary line per call, never one per row - and never any recipient identifier, only counts
        // and timing.
        logger.LogInformation(
            "Booking reminder due query returned {ReturnedCount} of up to {RequestedBatchSize} requested candidates in {ElapsedMilliseconds}ms.",
            items.Count,
            batchSize,
            stopwatch.ElapsedMilliseconds);

        return items;
    }
}
