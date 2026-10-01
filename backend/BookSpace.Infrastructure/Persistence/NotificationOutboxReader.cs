using System.Diagnostics;
using BookSpace.Application.Notifications;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BookSpace.Infrastructure.Persistence;

// Read-only due-batch query - see docs/background-jobs.md ("Due-batch query") for the full design,
// including why IgnoreQueryFilters() here is a narrow, justified exception to tenant isolation (a
// system-wide background read, not a web request), and how the generated SQL and index were verified
// against real SQL Server/LocalDB rather than assumed.
internal sealed class NotificationOutboxReader(BookSpaceDbContext dbContext, ILogger<NotificationOutboxReader> logger) : INotificationOutboxReader
{
    public async Task<IReadOnlyList<DueNotificationOutboxItem>> GetDueBatchAsync(int batchSize, CancellationToken cancellationToken)
    {
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "batchSize must be a positive number of items.");
        }

        var stopwatch = Stopwatch.StartNew();

        // IgnoreQueryFilters(): this is the one, narrow infrastructure exception to the global tenant
        // query filter described in docs/tenant-isolation.md - a system-wide background read that must
        // see every tenant's due items in one query has no HTTP request/JWT claim to scope it by, and the
        // filter fails closed (null TenantId matches nothing) rather than open. No other query in this
        // codebase should copy this without the same justification.
        //
        // DateTimeOffset.UtcNow is written directly inline (not captured into a local variable first) -
        // EF Core's SQL Server provider translates it to SYSUTCDATETIME() evaluated BY THE DATABASE at
        // execution time, not by this application instance's local clock. That is deliberate: several
        // instances share this one query, and the database's clock is the one thing they all agree on -
        // see docs/background-jobs.md ("Clock source for the due-batch query") for the full reasoning and
        // how this was confirmed against the actual generated SQL, not assumed.
        //
        // AsNoTracking() is redundant with the Select() projection below (EF never tracks a projected
        // non-entity result regardless), but kept anyway for defense-in-depth clarity, matching the same
        // choice in NotificationOutboxWriter.
        var items = await dbContext.NotificationOutboxItems
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => item.Status == NotificationOutboxStatus.Pending && item.AvailableAtUtc <= DateTimeOffset.UtcNow)
            // Oldest-due-first, with CreatedAtUtc then Id as stable tie-breakers for the (realistic, not
            // just theoretical) case of two items sharing the exact same AvailableAtUtc instant - e.g. a
            // future reminder job scheduling many bookings' reminders for the same computed instant. Id
            // alone would already be sufficient as a final tie-breaker (it is unique); CreatedAtUtc is
            // included first so that, among items that became due at the same moment, the one that was
            // actually enqueued earlier is still processed first - a more meaningful order than an
            // otherwise-arbitrary Guid comparison.
            .OrderBy(item => item.AvailableAtUtc)
            .ThenBy(item => item.CreatedAtUtc)
            .ThenBy(item => item.Id)
            .Take(batchSize)
            .Select(item => new DueNotificationOutboxItem(
                item.Id,
                item.TenantId,
                item.NotificationType,
                item.RecipientUserId,
                item.PayloadJson,
                item.AvailableAtUtc,
                item.CreatedAtUtc,
                item.AttemptCount))
            .ToListAsync(cancellationToken);

        // One summary line per call, never one per row - and never the payload/email/any recipient
        // identifier, only counts and timing.
        logger.LogInformation(
            "Notification outbox due-batch query returned {ReturnedCount} of up to {RequestedBatchSize} requested items in {ElapsedMilliseconds}ms.",
            items.Count,
            batchSize,
            stopwatch.ElapsedMilliseconds);

        return items;
    }
}
