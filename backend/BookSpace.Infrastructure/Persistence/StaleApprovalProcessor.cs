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

// Expires stale Pending approvals - see docs/background-jobs.md ("Stale-approval job") for the full
// design. Registered singleton-safe, same shape as NotificationOutboxProcessor: never holds a scoped
// IResourceBookingLock/BookSpaceDbContext/INotificationOutboxWriter itself, only IServiceScopeFactory, and
// creates a fresh IServiceScope - and therefore a fresh BookSpaceDbContext - for EVERY INDIVIDUAL
// CANDIDATE, not once per batch. That per-item granularity is what gives one candidate's unexpected
// database error no way to corrupt another candidate's DbContext/change tracker within the same batch,
// exactly the same reasoning NotificationOutboxProcessor already established for notification delivery.
internal sealed class StaleApprovalProcessor(
    IServiceScopeFactory scopeFactory,
    IStaleApprovalReader reader,
    IOptions<BackgroundJobsOptions> options,
    TimeProvider timeProvider,
    ILogger<StaleApprovalProcessor> logger) : IStaleApprovalProcessor
{
    private enum ItemOutcome { Expired, AlreadyCompleted, Conflict, Failed }

    public async Task<StaleApprovalCycleResult> ProcessDueApprovalsAsync(CancellationToken cancellationToken)
    {
        var staleOptions = options.Value.StaleApproval;
        if (!staleOptions.Enabled)
        {
            logger.LogInformation("Stale-approval processor skipped - BackgroundJobs:StaleApproval:Enabled is false.");
            return StaleApprovalCycleResult.Empty;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var candidates = await reader.GetDueApprovalsAsync(staleOptions.BatchSize, cancellationToken);
        if (candidates.Count == 0)
        {
            return StaleApprovalCycleResult.Empty;
        }

        int expired = 0, alreadyCompleted = 0, conflict = 0, failed = 0;
        foreach (var candidate in candidates)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation(
                    "Stale-approval batch processing stopped before ApprovalRequest {ApprovalRequestId} - cancellation requested; {RemainingCount} candidate(s) were not started.",
                    candidate.ApprovalRequestId,
                    candidates.Count - (expired + alreadyCompleted + conflict + failed));
                cancellationToken.ThrowIfCancellationRequested();
            }

            using var itemScope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["ApprovalRequestId"] = candidate.ApprovalRequestId,
                ["BookingId"] = candidate.BookingId,
                ["ResourceId"] = candidate.ResourceId,
                ["NotificationType"] = BookingNotificationTypes.ApprovalExpired,
            });

            ItemOutcome outcome;
            try
            {
                outcome = await ProcessOneAsync(candidate, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation(
                    "Stale-approval processing for ApprovalRequest {ApprovalRequestId} was cancelled mid-attempt; no state change was persisted.",
                    candidate.ApprovalRequestId);
                throw;
            }
            catch (ConflictException)
            {
                // Transient - the resource lock itself reported contention (e.g. a SQL Server deadlock/
                // lock-timeout, same classification ResourceBookingLock already uses for HTTP callers).
                // Never a failure: this candidate is simply retried on the next poll, since nothing here
                // committed.
                logger.LogInformation(
                    "Stale-approval processing for ApprovalRequest {ApprovalRequestId} hit a transient resource-lock conflict; it will be retried on the next poll.",
                    candidate.ApprovalRequestId);
                outcome = ItemOutcome.Conflict;
            }
            catch (Exception ex)
            {
                // Never an empty catch, and never conflated with the Conflict/AlreadyCompleted outcomes
                // above: an unexpected exception here (a database error, a bug) is isolated from the rest
                // of the batch, exactly like NotificationOutboxProcessor isolates one item's failure. The
                // candidate is simply still Pending and still past its own ExpiresAtUtc afterward, so the
                // very next poll's due query finds it again - no separate retry/attempt-count state is
                // introduced here, since re-discovering a still-Pending, still-expired row IS the retry;
                // there is nothing additional a persisted attempt counter would protect against that the
                // row's own Status doesn't already guarantee.
                logger.LogError(
                    ex, "Unexpected error processing stale ApprovalRequest {ApprovalRequestId}; continuing with the next candidate in this batch.", candidate.ApprovalRequestId);
                outcome = ItemOutcome.Failed;
            }

            switch (outcome)
            {
                case ItemOutcome.Expired: expired++; break;
                case ItemOutcome.AlreadyCompleted: alreadyCompleted++; break;
                case ItemOutcome.Conflict: conflict++; break;
                case ItemOutcome.Failed: failed++; break;
            }
        }

        logger.LogInformation(
            "Stale-approval processor completed. CandidateCount={CandidateCount} ExpiredCount={ExpiredCount} AlreadyCompletedCount={AlreadyCompletedCount} ConflictCount={ConflictCount} FailedCount={FailedCount}",
            candidates.Count, expired, alreadyCompleted, conflict, failed);

        // NotificationEnqueuedCount always equals ExpiredCount: the notification is enqueued inside the
        // exact same resource-lock transaction as the ApprovalRequest/Booking transition (see
        // ProcessOneAsync) - either all three land together, or none of them do, so there is never an
        // Expired outcome without its notification, nor a notification without the domain transition.
        return new StaleApprovalCycleResult(candidates.Count, expired, alreadyCompleted, conflict, failed, NotificationEnqueuedCount: expired);
    }

    // Never trusts the read-only candidate the reader found before any lock was taken - see
    // docs/background-jobs.md ("Stale-approval job - approve/reject race"). Mirrors
    // ApproveBookingCommandHandler/RejectBookingCommandHandler's own shape exactly: acquire
    // IResourceBookingLock for the candidate's ResourceId, and only THEN load the real, current
    // ApprovalRequest/Booking rows - the lock serializes this against a concurrent HTTP approve/reject for
    // the same resource, the same guarantee that already protects Approve-vs-Reject and Approve-vs-Create.
    //
    // No second ExpiresAtUtc re-check under the lock: unlike Status (which a racing Approve/Reject call can
    // genuinely change), ExpiresAtUtc is a fixed value once written - time only moves forward, so a request
    // already past its own ExpiresAtUtc when the reader found it is still past it here, regardless of how
    // much time elapsed acquiring the lock. Re-checking Status is what actually matters, and is what
    // decides AlreadyCompleted below.
    private async Task<ItemOutcome> ProcessOneAsync(DueStaleApproval candidate, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();
        var resourceBookingLock = scope.ServiceProvider.GetRequiredService<IResourceBookingLock>();
        var notificationOutboxWriter = scope.ServiceProvider.GetRequiredService<INotificationOutboxWriter>();

        return await resourceBookingLock.RunExclusiveAsync(candidate.ResourceId, async ct =>
        {
            // IgnoreQueryFilters() + explicit TenantId: same narrow, documented background-processing
            // exception to the global tenant filter as BookingReminderEligibilityChecker - this must never
            // resolve a row belonging to a different tenant than the one the due query found it under.
            var approvalRequest = await dbContext.ApprovalRequests
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(ar => ar.TenantId == candidate.TenantId && ar.Id == candidate.ApprovalRequestId, ct);
            var booking = await dbContext.Bookings
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(b => b.TenantId == candidate.TenantId && b.Id == candidate.BookingId, ct);

            if (approvalRequest is null || booking is null)
            {
                // Defensive only - nothing in this codebase deletes these rows today.
                logger.LogWarning("ApprovalRequest {ApprovalRequestId} or its Booking was not found when processing started - skipping.", candidate.ApprovalRequestId);
                return ItemOutcome.AlreadyCompleted;
            }

            if (approvalRequest.Status != ApprovalStatus.Pending || booking.Status != BookingStatus.Pending)
            {
                // The race described in docs/background-jobs.md ("approve/reject race") going the OTHER
                // way: an HTTP Approve/Reject call reached this exact booking first, under the same
                // resource lock, and already committed. Nothing to do - this is the correct, expected
                // outcome of that race, never an error.
                logger.LogInformation(
                    "ApprovalRequest {ApprovalRequestId} is no longer Pending (ApprovalStatus={ApprovalStatus}, BookingStatus={BookingStatus}) - an approve/reject decision already won the race; skipping.",
                    candidate.ApprovalRequestId, approvalRequest.Status, booking.Status);
                return ItemOutcome.AlreadyCompleted;
            }

            var now = timeProvider.GetUtcNow();
            approvalRequest.Status = ApprovalStatus.Expired;
            approvalRequest.DecidedAtUtc = now;
            // ApproverId stays null - nobody decided this; a human-assigned ApproverId would wrongly imply
            // an approver acted. DecisionNote is set to make the automatic nature legible to anyone
            // reading this row later, including the Angular approval queue/history view.
            approvalRequest.DecisionNote = "Automatically expired - no approve/reject decision was made before the approval window closed.";
            booking.Status = BookingStatus.Expired;

            // EnqueueAsync calls SaveChangesAsync itself, committing the ApprovalRequest/Booking mutations
            // staged above AND the new NotificationOutboxItem row together, inside the SAME transaction
            // IResourceBookingLock already opened for this resource - see docs/background-jobs.md ("Stale-
            // approval job - atomicity"). A failure here rolls back all three together via the lock's own
            // catch-all.
            await notificationOutboxWriter.EnqueueAsync(BookingNotificationFactory.ApprovalExpired(booking, approvalRequest.Id), ct);

            logger.LogInformation(
                "Stale approval expired. ApprovalRequestId={ApprovalRequestId} BookingId={BookingId} TenantId={TenantId}",
                approvalRequest.Id, booking.Id, booking.TenantId);
            return ItemOutcome.Expired;
        }, cancellationToken);
    }
}
