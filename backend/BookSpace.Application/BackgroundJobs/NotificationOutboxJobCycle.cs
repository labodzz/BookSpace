using BookSpace.Application.Email;
using BookSpace.Application.Logging;
using BookSpace.Application.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookSpace.Application.BackgroundJobs;

// The real WP-8 notification-delivery cycle - replaces NoOpBackgroundJobCycle as the one
// IBackgroundJobCycle BackgroundJobsWorker (BookSpace.Api) resolves from its per-cycle scope. See
// docs/background-jobs.md ("Notification outbox cycle") for the full runtime story: worker -> lease ->
// this cycle -> IStaleApprovalProcessor -> [IReminderProducer -> INotificationOutboxWriter] ->
// INotificationOutboxReader -> INotificationOutboxProcessor -> INotificationSender.
//
// This class is deliberately thin: it owns none of AttemptCount/backoff/AvailableAtUtc/Sent/DeadLetter/
// Suppressed/transient-vs-permanent classification/SMTP sending/reminder-candidate-selection/stale-
// approval-expiration - INotificationOutboxProcessor, IReminderProducer, and IStaleApprovalProcessor
// already own all of that, unchanged. The only things this cycle adds are orchestration (expire stale
// approvals, produce due reminders, then one bounded due-batch dispatch query per poll), the Email:Enabled
// short-circuit around dispatch only, job-run correlation, and the one summary log line a cycle emits when
// it completes normally.
//
// Kept this name rather than renamed despite now owning four phases (expire stale approvals, mark
// no-shows, produce reminders, then dispatch the outbox): all four exist in service of the same one job -
// making sure the notification outbox gets populated (by whichever business event decided a notification
// is needed) and drained - so "NotificationOutboxJobCycle" still describes the whole of what this class
// does, not just a quarter of it. See docs/background-jobs.md ("Reminder job - cycle ordering", "Stale-
// approval job - cycle ordering", and "No-show job - cycle ordering") for why this was a deliberate
// decision, not an oversight.
//
// Safe to register Scoped as before: every dependency here (the reader, the processor, the reminder
// producer, IOptions<T>, IBackgroundJobInstanceIdentity, ICorrelationIdContext, TimeProvider, ILogger) is
// itself either Scoped or Singleton-safe, and this type is only ever resolved from the fresh per-cycle
// scope BackgroundJobsWorker already creates - it is never held by that singleton worker itself.
public sealed class NotificationOutboxJobCycle(
    IStaleApprovalProcessor staleApprovalProcessor,
    INoShowProcessor noShowProcessor,
    IReminderProducer reminderProducer,
    INotificationOutboxReader reader,
    INotificationOutboxProcessor processor,
    IOptions<BackgroundJobsOptions> jobOptions,
    IOptions<EmailOptions> emailOptions,
    IBackgroundJobInstanceIdentity instanceIdentity,
    ICorrelationIdContext correlationIdContext,
    TimeProvider timeProvider,
    ILogger<NotificationOutboxJobCycle> logger) : IBackgroundJobCycle
{
    // Distinct from BackgroundJobsWorker.PrimaryCycleJobName (the lease's own name, which identifies
    // which job's mutual-exclusion lease is being acquired - a worker-level concern) - this is purely a
    // self-reported label for this cycle's own log lines, so a future second IBackgroundJobCycle
    // implementation would carry its own, different JobName here without needing a second lease name.
    public const string JobName = "notification-outbox";

    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString();
        correlationIdContext.Set(correlationId);

        // The same property name CorrelationIdMiddleware uses for an HTTP request (see
        // docs/background-jobs.md, "Job-run and item correlation") - ILogger.BeginScope rather than
        // Serilog.Context.LogContext directly, since this layer (unlike BookSpace.Api) has no direct
        // Serilog package reference; Serilog's own Microsoft.Extensions.Logging bridge enriches log
        // events with BeginScope's dictionary entries identically either way.
        using var runScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId,
            ["JobName"] = JobName,
            ["JobOwnerId"] = instanceIdentity.OwnerId,
        });

        var startTimestamp = timeProvider.GetTimestamp();

        // A second line (besides the end-of-cycle summary) carrying the same CorrelationId/JobName/
        // JobOwnerId scope - lets the run/item correlation chain (docs/background-jobs.md) actually be
        // followed from "a cycle started" through to "it finished" in a log stream, and gives tooling two
        // real points to confirm share one id, not just one.
        logger.LogDebug("Notification outbox cycle started.");

        cancellationToken.ThrowIfCancellationRequested();

        // Runs first, and - like the steps below - regardless of Email:Enabled: expiring a stale approval
        // (and releasing the booking's capacity) is a business-level state transition independent of
        // whether the email transport happens to be configured right now. Isolated with its own try/catch:
        // an unexpected failure here must never block the no-show step, the reminder producer, or the
        // dispatch step that follow - see docs/background-jobs.md ("Stale-approval job - cycle ordering").
        var staleApprovalResult = await RunStaleApprovalProcessorSafelyAsync(cancellationToken);

        // Runs second, immediately after stale-approval expiry and before reminder production - see
        // docs/background-jobs.md ("No-show job - cycle ordering") for why this exact position: a booking
        // already past its no-show deadline has no business being reminded about a start time that has
        // already come and gone, so no-show marking must happen before the reminder producer's own due
        // query runs. Also runs regardless of Email:Enabled, for the identical reason stale-approval does -
        // marking a booking NoShow (and releasing its capacity) is a business-level transition, not an
        // email-delivery concern. Isolated with its own try/catch: an unexpected failure here must never
        // block the reminder producer or the dispatch step that follow.
        var noShowResult = await RunNoShowProcessorSafelyAsync(cancellationToken);

        // Runs regardless of Email:Enabled, deliberately - producing a reminder (a row in the
        // notification outbox) is a business-level decision independent of whether the email TRANSPORT
        // happens to be configured right now; see docs/background-jobs.md ("Reminder job - relationship
        // to Email:Enabled"). Isolated from the dispatch step below: an unexpected failure here is logged
        // and this cycle still goes on to dispatch whatever notification items are already due - a
        // reminder-production bug must never look like (or cause) a dispatch outage.
        var reminderResult = await RunReminderProducerSafelyAsync(cancellationToken);

        if (!emailOptions.Value.Enabled)
        {
            // Skips the due-batch query entirely - not just the processor/sender - so a disabled
            // deployment never reads, never writes, never opens a network connection, and leaves every
            // Pending item completely untouched (AttemptCount unchanged, nothing dead-lettered). Relying
            // on SmtpNotificationSender's own Enabled guard instead would still have let the processor
            // mark real due items DeadLettered the first time this cycle ever ran with items present -
            // see docs/background-jobs.md ("Email disabled behavior").
            logger.LogInformation("Notification outbox cycle dispatch skipped - Email:Enabled is false.");
            LogSummary(correlationId, staleApprovalResult, noShowResult, reminderResult, jobOptions.Value.BatchSize, pickedCount: 0, results: [], timeProvider.GetElapsedTime(startTimestamp));
            return;
        }

        var batchSize = jobOptions.Value.BatchSize;
        var batch = await reader.GetDueBatchAsync(batchSize, cancellationToken);

        if (batch.Count == 0)
        {
            LogSummary(correlationId, staleApprovalResult, noShowResult, reminderResult, batchSize, pickedCount: 0, results: [], timeProvider.GetElapsedTime(startTimestamp));
            return;
        }

        var results = await processor.ProcessBatchAsync(batch, cancellationToken);

        if (cancellationToken.IsCancellationRequested)
        {
            // ProcessBatchAsync itself never throws for an ordinary, expected cancellation - it just
            // stops and returns whatever it completed. This is the one place that turns "the batch was
            // cut short" back into a real OperationCanceledException, so BackgroundJobsWorker's own
            // shutdown-vs-lease-lost handling (RunCycleAsync's two separate catch clauses) still sees
            // exactly the signal it expects - never a fake "completed successfully" summary line for a
            // cycle that was actually interrupted.
            logger.LogInformation(
                "Notification outbox cycle interrupted - cancellation requested after {CompletedCount} of {PickedCount} item(s) in this batch were started.",
                results.Count,
                batch.Count);
            cancellationToken.ThrowIfCancellationRequested();
        }

        LogSummary(correlationId, staleApprovalResult, noShowResult, reminderResult, batchSize, batch.Count, results, timeProvider.GetElapsedTime(startTimestamp));
    }

    // Same isolation shape as RunReminderProducerSafelyAsync below: cancellation always propagates (a lost
    // lease or application shutdown must abort the whole cycle, including the reminder producer and
    // dispatch steps that haven't run yet), but an unexpected exception is logged and swallowed into an
    // empty result so one job's bug can never block the others sharing this cycle.
    private async Task<StaleApprovalCycleResult> RunStaleApprovalProcessorSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await staleApprovalProcessor.ProcessDueApprovalsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex, "Stale-approval processor failed unexpectedly for this cycle; continuing to the reminder producer and dispatch.");
            return StaleApprovalCycleResult.Empty;
        }
    }

    // Same isolation shape as RunStaleApprovalProcessorSafelyAsync/RunReminderProducerSafelyAsync:
    // cancellation always propagates (a lost lease or application shutdown must abort the whole cycle,
    // including the reminder producer and dispatch steps that haven't run yet), but an unexpected exception
    // is logged and swallowed into an empty result so one job's bug can never block the others sharing this
    // cycle.
    private async Task<NoShowCycleResult> RunNoShowProcessorSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await noShowProcessor.ProcessDueNoShowsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex, "No-show processor failed unexpectedly for this cycle; continuing to the reminder producer and dispatch.");
            return NoShowCycleResult.Empty;
        }
    }

    // Cancellation (application shutdown or a lost job lease) always propagates immediately, aborting the
    // whole cycle - never absorbed here, exactly like every other cancellation path in this class and in
    // BackgroundJobsWorker. An unexpected exception, by contrast, is logged and swallowed into an empty
    // result: dispatching already-enqueued notification items is a completely independent concern from
    // producing new reminders, and is safe to continue - see docs/background-jobs.md ("Reminder job -
    // failure isolation") for why this is a documented, deliberate decision, not an oversight that could
    // silently hide a producer bug (the LogError below ensures it never does).
    private async Task<ReminderProducerResult> RunReminderProducerSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await reminderProducer.ProduceDueRemindersAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex, "Reminder producer failed unexpectedly for this cycle; continuing to dispatch already-enqueued notification items.");
            return ReminderProducerResult.Empty;
        }
    }

    // Exactly one structured line per normally-completed cycle - every count below comes from the
    // structured ReminderProducerResult/NotificationOutboxBatchItemResult data the producer/processor
    // returned, never from parsing a log message or an exception's text. See docs/background-jobs.md
    // ("Notification outbox cycle") for why PermanentFailureCount and DeadLetterCount are reported as the
    // same number in this implementation: NotificationOutboxItemOutcome does not distinguish "a permanent
    // send failure" from "a transient failure that just exhausted MaxNotificationAttempts" - both already
    // collapse into the single DeadLettered outcome inside NotificationOutboxProcessor, and splitting that
    // further here would mean reinterpreting/duplicating classification logic the processor already owns,
    // not just reporting it.
    private void LogSummary(
        string correlationId,
        StaleApprovalCycleResult staleApprovalResult,
        NoShowCycleResult noShowResult,
        ReminderProducerResult reminderResult,
        int requestedBatchSize,
        int pickedCount,
        IReadOnlyList<NotificationOutboxBatchItemResult> results,
        TimeSpan elapsed)
    {
        var succeededCount = results.Count(result => result.Outcome == NotificationOutboxItemOutcome.Sent);
        var transientFailureCount = results.Count(result => result.Outcome == NotificationOutboxItemOutcome.RetryScheduled);
        var deadLetterCount = results.Count(result => result.Outcome == NotificationOutboxItemOutcome.DeadLettered);
        var suppressedCount = results.Count(result => result.Outcome == NotificationOutboxItemOutcome.Suppressed);
        var unexpectedFailureCount = results.Count(result => result.Outcome is null);

        logger.LogInformation(
            "Notification outbox cycle completed. JobName={JobName} CorrelationId={CorrelationId} "
            + "StaleApprovalCandidateCount={StaleApprovalCandidateCount} StaleApprovalExpiredCount={StaleApprovalExpiredCount} "
            + "StaleApprovalAlreadyCompletedCount={StaleApprovalAlreadyCompletedCount} StaleApprovalConflictCount={StaleApprovalConflictCount} "
            + "StaleApprovalFailedCount={StaleApprovalFailedCount} StaleApprovalNotificationEnqueuedCount={StaleApprovalNotificationEnqueuedCount} "
            + "NoShowCandidateCount={NoShowCandidateCount} NoShowMarkedCount={NoShowMarkedCount} "
            + "NoShowAlreadyCompletedCount={NoShowAlreadyCompletedCount} NoShowCheckedInCount={NoShowCheckedInCount} "
            + "NoShowNotYetDueCount={NoShowNotYetDueCount} NoShowConflictCount={NoShowConflictCount} "
            + "NoShowFailedCount={NoShowFailedCount} NoShowNotificationEnqueuedCount={NoShowNotificationEnqueuedCount} "
            + "ReminderCandidateCount={ReminderCandidateCount} ReminderEnqueuedCount={ReminderEnqueuedCount} "
            + "ReminderDuplicateCount={ReminderDuplicateCount} "
            + "RequestedBatchSize={RequestedBatchSize} PickedCount={PickedCount} SucceededCount={SucceededCount} "
            + "TransientFailureCount={TransientFailureCount} PermanentFailureCount={PermanentFailureCount} "
            + "DeadLetterCount={DeadLetterCount} SuppressedCount={SuppressedCount} "
            + "UnexpectedFailureCount={UnexpectedFailureCount} ElapsedMilliseconds={ElapsedMilliseconds}",
            JobName,
            correlationId,
            staleApprovalResult.CandidateCount,
            staleApprovalResult.ExpiredCount,
            staleApprovalResult.AlreadyCompletedCount,
            staleApprovalResult.ConflictCount,
            staleApprovalResult.FailedCount,
            staleApprovalResult.NotificationEnqueuedCount,
            noShowResult.CandidateCount,
            noShowResult.MarkedNoShowCount,
            noShowResult.AlreadyCompletedCount,
            noShowResult.CheckedInCount,
            noShowResult.NotYetDueCount,
            noShowResult.ConflictCount,
            noShowResult.FailedCount,
            noShowResult.NotificationEnqueuedCount,
            reminderResult.CandidateCount,
            reminderResult.EnqueuedCount,
            reminderResult.DuplicateCount,
            requestedBatchSize,
            pickedCount,
            succeededCount,
            transientFailureCount,
            deadLetterCount, // PermanentFailureCount - see the doc comment above for why this equals DeadLetterCount here
            deadLetterCount,
            suppressedCount,
            unexpectedFailureCount,
            elapsed.TotalMilliseconds);
    }
}
