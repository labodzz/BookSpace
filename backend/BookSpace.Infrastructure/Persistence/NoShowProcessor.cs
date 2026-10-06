using BookSpace.Application.BackgroundJobs;
using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using BookSpace.Application.Notifications;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookSpace.Infrastructure.Persistence;

// Marks Confirmed-but-never-checked-in bookings as NoShow once their deadline passes - see
// docs/background-jobs.md ("No-show job") for the full design. Registered singleton-safe, same shape as
// StaleApprovalProcessor/NotificationOutboxProcessor: never holds a scoped IResourceBookingLock/
// BookSpaceDbContext/ITenantRepository/INotificationOutboxWriter itself, only IServiceScopeFactory, and
// creates a fresh IServiceScope - and therefore a fresh BookSpaceDbContext - for EVERY INDIVIDUAL
// CANDIDATE, not once per batch. That per-item granularity is what gives one candidate's unexpected
// database error no way to corrupt another candidate's DbContext/change tracker within the same batch.
internal sealed class NoShowProcessor(
    IServiceScopeFactory scopeFactory,
    INoShowReader reader,
    IOptions<BackgroundJobsOptions> options,
    TimeProvider timeProvider,
    ILogger<NoShowProcessor> logger) : INoShowProcessor
{
    private enum ItemOutcome { MarkedNoShow, AlreadyCompleted, CheckedIn, NotYetDue, Conflict, Failed }

    public async Task<NoShowCycleResult> ProcessDueNoShowsAsync(CancellationToken cancellationToken)
    {
        var noShowOptions = options.Value.NoShow;
        if (!noShowOptions.Enabled)
        {
            logger.LogInformation("No-show processor skipped - BackgroundJobs:NoShow:Enabled is false.");
            return NoShowCycleResult.Empty;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var candidates = await reader.GetDueNoShowsAsync(noShowOptions.BatchSize, cancellationToken);
        if (candidates.Count == 0)
        {
            return NoShowCycleResult.Empty;
        }

        int markedNoShow = 0, alreadyCompleted = 0, checkedIn = 0, notYetDue = 0, conflict = 0, failed = 0;
        foreach (var candidate in candidates)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation(
                    "No-show batch processing stopped before Booking {BookingId} - cancellation requested; {RemainingCount} candidate(s) were not started.",
                    candidate.BookingId,
                    candidates.Count - (markedNoShow + alreadyCompleted + checkedIn + notYetDue + conflict + failed));
                cancellationToken.ThrowIfCancellationRequested();
            }

            using var itemScope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["BookingId"] = candidate.BookingId,
                ["ResourceId"] = candidate.ResourceId,
                ["NotificationType"] = BookingNotificationTypes.NoShow,
            });

            ItemOutcome outcome;
            try
            {
                outcome = await ProcessOneAsync(candidate, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation(
                    "No-show processing for Booking {BookingId} was cancelled mid-attempt; no state change was persisted.",
                    candidate.BookingId);
                throw;
            }
            catch (ConflictException)
            {
                // Transient - the resource lock itself reported contention (e.g. a SQL Server deadlock/
                // lock-timeout, same classification ResourceBookingLock already uses for HTTP callers).
                // Never a failure: this candidate is simply retried on the next poll, since nothing here
                // committed.
                logger.LogInformation(
                    "No-show processing for Booking {BookingId} hit a transient resource-lock conflict; it will be retried on the next poll.",
                    candidate.BookingId);
                outcome = ItemOutcome.Conflict;
            }
            catch (Exception ex)
            {
                // Never an empty catch, and never conflated with the Conflict/AlreadyCompleted outcomes
                // above: an unexpected exception here (a database error, a bug) is isolated from the rest
                // of the batch, exactly like StaleApprovalProcessor/NotificationOutboxProcessor isolate one
                // item's failure. The candidate is simply still due afterward (unless something else
                // resolved it), so the very next poll's due query finds it again - no separate retry/
                // attempt-count state is introduced here.
                logger.LogError(
                    ex, "Unexpected error processing no-show candidate Booking {BookingId}; continuing with the next candidate in this batch.", candidate.BookingId);
                outcome = ItemOutcome.Failed;
            }

            switch (outcome)
            {
                case ItemOutcome.MarkedNoShow: markedNoShow++; break;
                case ItemOutcome.AlreadyCompleted: alreadyCompleted++; break;
                case ItemOutcome.CheckedIn: checkedIn++; break;
                case ItemOutcome.NotYetDue: notYetDue++; break;
                case ItemOutcome.Conflict: conflict++; break;
                case ItemOutcome.Failed: failed++; break;
            }
        }

        logger.LogInformation(
            "No-show processor completed. CandidateCount={CandidateCount} MarkedNoShowCount={MarkedNoShowCount} "
            + "AlreadyCompletedCount={AlreadyCompletedCount} CheckedInCount={CheckedInCount} NotYetDueCount={NotYetDueCount} "
            + "ConflictCount={ConflictCount} FailedCount={FailedCount}",
            candidates.Count, markedNoShow, alreadyCompleted, checkedIn, notYetDue, conflict, failed);

        // NotificationEnqueuedCount always equals MarkedNoShowCount: the notification is enqueued inside
        // the exact same resource-lock transaction as the Booking transition (see ProcessOneAsync) -
        // either both land together, or neither does, so there is never a MarkedNoShow outcome without its
        // notification, nor a notification without the domain transition.
        return new NoShowCycleResult(candidates.Count, markedNoShow, alreadyCompleted, checkedIn, notYetDue, conflict, failed, NotificationEnqueuedCount: markedNoShow);
    }

    // Never trusts the read-only candidate the reader found before any lock was taken - see
    // docs/background-jobs.md ("No-show job - race protection"). Mirrors StaleApprovalProcessor's own
    // shape: acquire IResourceBookingLock for the candidate's ResourceId first, and only THEN load the
    // real, current Booking row AND a freshly-read Tenant.NoShowGraceMinutes - the lock serializes this
    // against a concurrent HTTP check-in or cancel for the same resource, the SAME guarantee that already
    // protects Check-in-vs-Cancel (see docs/bookings-and-concurrency.md, "The check-in/cancel race - now
    // closed"). Re-deriving the deadline from a FRESH Tenant read (not trusting any value implied by the
    // pre-lock candidate) is what makes the NotYetDue outcome possible if an operator changed
    // NoShowGraceMinutes between the due query and this item's turn.
    private async Task<ItemOutcome> ProcessOneAsync(DueNoShowBooking candidate, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();
        var resourceBookingLock = scope.ServiceProvider.GetRequiredService<IResourceBookingLock>();
        var tenantRepository = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        var notificationOutboxWriter = scope.ServiceProvider.GetRequiredService<INotificationOutboxWriter>();

        return await resourceBookingLock.RunExclusiveAsync(candidate.ResourceId, async ct =>
        {
            // IgnoreQueryFilters() + explicit TenantId: same narrow, documented background-processing
            // exception to the global tenant filter as StaleApprovalProcessor/BookingReminderEligibilityChecker
            // - this must never resolve a row belonging to a different tenant than the one the due query
            // found it under.
            var booking = await dbContext.Bookings
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(b => b.TenantId == candidate.TenantId && b.Id == candidate.BookingId, ct);

            if (booking is null)
            {
                // Defensive only - nothing in this codebase deletes Booking rows today.
                logger.LogWarning("Booking {BookingId} was not found when no-show processing started - skipping.", candidate.BookingId);
                return ItemOutcome.AlreadyCompleted;
            }

            // Checked BEFORE the status check and classified separately from AlreadyCompleted: this is the
            // specific "check-in won the race" outcome CheckInBookingCommandHandler's own lock-sharing
            // exists to guarantee is possible, not a generic catch-all.
            if (booking.CheckedInAtUtc is not null)
            {
                logger.LogInformation("Booking {BookingId} was checked in before no-show processing reached it - skipping.", candidate.BookingId);
                return ItemOutcome.CheckedIn;
            }

            if (booking.Status != BookingStatus.Confirmed)
            {
                // Most commonly a cancel decision won the race under the same lock - see
                // docs/bookings-and-concurrency.md ("No-show processing - the cancel-wins outcome").
                logger.LogInformation(
                    "Booking {BookingId} is no longer Confirmed (Status={Status}) - some other decision already won the race; skipping.",
                    candidate.BookingId, booking.Status);
                return ItemOutcome.AlreadyCompleted;
            }

            var tenant = await tenantRepository.FindByIdAsync(candidate.TenantId, ct)
                ?? throw new InvalidOperationException($"Tenant {candidate.TenantId} for booking {candidate.BookingId} was not found.");

            var nowUtc = timeProvider.GetUtcNow();
            var deadlineUtc = booking.StartUtc.AddMinutes(tenant.NoShowGraceMinutes);
            if (nowUtc <= deadlineUtc)
            {
                // Strictly the mirror image of CheckInEligibilityChecker's own inclusive boundary
                // (nowUtc <= deadline still allows check-in) - at the exact deadline instant, no-show must
                // never be able to win.
                logger.LogInformation(
                    "Booking {BookingId} is no longer past its no-show deadline after re-checking under the lock - skipping.", candidate.BookingId);
                return ItemOutcome.NotYetDue;
            }

            booking.Status = BookingStatus.NoShow;
            // CheckedInAtUtc stays null - that absence IS the no-show signal; never set it, never fake a
            // check-in the member never performed.

            // EnqueueAsync calls SaveChangesAsync itself, committing the Booking mutation staged above AND
            // the new NotificationOutboxItem row together, inside the SAME transaction IResourceBookingLock
            // already opened for this resource - see docs/background-jobs.md ("No-show job - atomicity"). A
            // failure here rolls back both together via the lock's own catch-all.
            await notificationOutboxWriter.EnqueueAsync(BookingNotificationFactory.NoShow(booking), ct);

            logger.LogInformation("Booking marked as no-show. BookingId={BookingId} TenantId={TenantId}", booking.Id, booking.TenantId);
            return ItemOutcome.MarkedNoShow;
        }, cancellationToken);
    }
}
