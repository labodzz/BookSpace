using BookSpace.Application.BackgroundJobs;
using BookSpace.Application.Notifications;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookSpace.Infrastructure.Persistence;

// Per-item notification delivery with persisted retry/backoff - see docs/background-jobs.md ("Per-item
// processing and retry") for the full design. Registered as a singleton-safe component: like
// JobLeaseCoordinator and BackgroundJobsWorker, it never holds a scoped IJobLeaseStore/DbContext/sender
// itself, only IServiceScopeFactory, and creates a fresh IServiceScope - and therefore a fresh
// BookSpaceDbContext AND a fresh INotificationSender - for EVERY INDIVIDUAL ITEM, not once per batch. That
// per-item (not per-batch) granularity is what gives one item's unexpected database error no way to
// corrupt another item's DbContext/change tracker, and what guarantees each item commits (or doesn't)
// completely independently of every other item in the same batch.
internal sealed class NotificationOutboxProcessor(
    IServiceScopeFactory scopeFactory,
    IOptions<BackgroundJobsOptions> options,
    TimeProvider timeProvider,
    ILogger<NotificationOutboxProcessor> logger) : INotificationOutboxProcessor
{
    public async Task<NotificationOutboxItemOutcome> ProcessItemAsync(DueNotificationOutboxItem item, CancellationToken cancellationToken)
    {
        // Never start a new delivery attempt against an already-cancelled token - nothing below this
        // point has touched the database yet, so there is nothing to undo.
        cancellationToken.ThrowIfCancellationRequested();

        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<INotificationSender>();

        // IgnoreQueryFilters(): same narrow, justified exception as NotificationOutboxReader - this is
        // the system-wide background processor side of the exact same due item the reader already found
        // across every tenant, not a web request. See docs/tenant-isolation.md.
        var entity = await dbContext.NotificationOutboxItems
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(candidate => candidate.Id == item.Id, cancellationToken);

        if (entity is null)
        {
            // Defensive only - nothing in this codebase deletes an outbox row today. Not an error: the
            // item is simply no longer there to process.
            logger.LogWarning(
                "Notification outbox item {OutboxItemId} was not found when processing started - skipping.", item.Id);
            return NotificationOutboxItemOutcome.Skipped;
        }

        // The attempt counter is bumped the moment a real delivery attempt is about to start - "started",
        // not "succeeded" - see docs/background-jobs.md ("AttemptCount semantics"). Still only in memory:
        // nothing is persisted until the attempt actually completes (see the catch below for why).
        entity.AttemptCount += 1;
        var message = new NotificationMessage(entity.Id, entity.NotificationType, entity.RecipientUserId, entity.PayloadJson);

        NotificationSendResult result;
        try
        {
            result = await sender.SendAsync(message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelled during the actual delivery attempt (application shutdown, typically) - never
            // save anything. The in-memory AttemptCount bump above is discarded along with this
            // DbContext, so to the next cycle this item looks exactly as if this attempt never happened -
            // see docs/background-jobs.md ("Cancellation behavior") for why that is deliberate, not
            // merely "didn't get around to saving."
            logger.LogInformation(
                "Notification outbox delivery for {OutboxItemId} was cancelled mid-attempt; no state change was persisted.", item.Id);
            throw;
        }

        var nowUtc = timeProvider.GetUtcNow();
        entity.LastAttemptAtUtc = nowUtc;

        NotificationOutboxItemOutcome outcome;
        switch (result.Outcome)
        {
            case NotificationSendOutcome.Success:
                entity.Status = NotificationOutboxStatus.Sent;
                entity.LastError = null;
                outcome = NotificationOutboxItemOutcome.Sent;
                break;

            case NotificationSendOutcome.PermanentFailure:
                entity.Status = NotificationOutboxStatus.DeadLettered;
                entity.LastError = SanitizeError(result.ErrorMessage);
                outcome = NotificationOutboxItemOutcome.DeadLettered;
                break;

            case NotificationSendOutcome.TransientFailure:
                entity.LastError = SanitizeError(result.ErrorMessage);
                if (entity.AttemptCount >= options.Value.MaxNotificationAttempts)
                {
                    // This was the last allowed attempt (see docs/background-jobs.md,
                    // "AttemptCount semantics" for the off-by-one reasoning) - there is never an attempt
                    // MaxNotificationAttempts + 1.
                    entity.Status = NotificationOutboxStatus.DeadLettered;
                    outcome = NotificationOutboxItemOutcome.DeadLettered;
                }
                else
                {
                    var delay = NotificationRetryBackoff.ComputeDelay(
                        entity.AttemptCount, options.Value.InitialRetryDelaySeconds, options.Value.MaxRetryDelaySeconds);
                    entity.AvailableAtUtc = nowUtc + delay;
                    outcome = NotificationOutboxItemOutcome.RetryScheduled;
                }

                break;

            default:
                throw new InvalidOperationException($"Unhandled {nameof(NotificationSendOutcome)}: {result.Outcome}");
        }

        // CancellationToken.None deliberately: the delivery attempt already completed and its outcome is
        // known - persisting that outcome is the one thing that must not be cut short by a shutdown
        // signal arriving in this exact instant, mirroring JobLeaseCoordinator's own release-on-dispose
        // reasoning. A failure here (a genuine database error) is intentionally NOT caught - it
        // propagates to the caller (ProcessBatchAsync's per-item try/catch) as an unexpected error, never
        // reported as a "provider transient failure" the way the switch above reports one.
        await dbContext.SaveChangesAsync(CancellationToken.None);

        LogOutcome(item, entity.AttemptCount, outcome, entity.AvailableAtUtc);
        return outcome;
    }

    public async Task ProcessBatchAsync(IReadOnlyList<DueNotificationOutboxItem> batch, CancellationToken cancellationToken)
    {
        for (var index = 0; index < batch.Count; index++)
        {
            var item = batch[index];

            if (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation(
                    "Notification outbox batch processing stopped before item {OutboxItemId} - cancellation requested; {RemainingCount} item(s) in this batch were not started.",
                    item.Id,
                    batch.Count - index);
                return;
            }

            try
            {
                await ProcessItemAsync(item, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Already logged (with more detail) inside ProcessItemAsync - this just stops the loop
                // rather than starting the next item.
                return;
            }
            catch (Exception ex)
            {
                // Never an empty catch, and never reported as a delivery outcome: an unexpected exception
                // here (a database error loading/saving the item, a bug) is logged distinctly from the
                // structured Sent/RetryScheduled/DeadLettered outcome logging in ProcessItemAsync, and is
                // isolated from the rest of the batch exactly like BackgroundJobsWorker isolates one
                // cycle's failure from the next poll.
                logger.LogError(
                    ex, "Unexpected error processing notification outbox item {OutboxItemId}; continuing with the next item in this batch.", item.Id);
            }
        }
    }

    // Only ever truncates what NotificationSendResult.ErrorMessage explicitly provided - never a raw
    // exception's message or stack trace, which callers of this processor must never pass through as
    // ErrorMessage in the first place. See docs/background-jobs.md ("LastError").
    private static string? SanitizeError(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage))
        {
            return errorMessage;
        }

        return errorMessage.Length <= NotificationOutboxItem.MaxLastErrorLength
            ? errorMessage
            : errorMessage[..NotificationOutboxItem.MaxLastErrorLength];
    }

    // One structured line per item - never per row of anything else, never the payload/recipient
    // email/any other PII. See docs/background-jobs.md ("Logging").
    private void LogOutcome(DueNotificationOutboxItem item, int attemptCount, NotificationOutboxItemOutcome outcome, DateTimeOffset availableAtUtc)
    {
        switch (outcome)
        {
            case NotificationOutboxItemOutcome.Sent:
                logger.LogInformation(
                    "Notification outbox item processed. OutboxItemId={OutboxItemId} TenantId={TenantId} NotificationType={NotificationType} AttemptCount={AttemptCount} Outcome=Sent",
                    item.Id, item.TenantId, item.NotificationType, attemptCount);
                break;
            case NotificationOutboxItemOutcome.RetryScheduled:
                logger.LogWarning(
                    "Notification outbox item processed. OutboxItemId={OutboxItemId} TenantId={TenantId} NotificationType={NotificationType} AttemptCount={AttemptCount} Outcome=RetryScheduled NextAttemptAtUtc={NextAttemptAtUtc}",
                    item.Id, item.TenantId, item.NotificationType, attemptCount, availableAtUtc);
                break;
            case NotificationOutboxItemOutcome.DeadLettered:
                logger.LogError(
                    "Notification outbox item processed. OutboxItemId={OutboxItemId} TenantId={TenantId} NotificationType={NotificationType} AttemptCount={AttemptCount} Outcome=DeadLettered",
                    item.Id, item.TenantId, item.NotificationType, attemptCount);
                break;
        }
    }
}
