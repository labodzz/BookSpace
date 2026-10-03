using BookSpace.Application.Email;
using BookSpace.Application.Logging;
using BookSpace.Application.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookSpace.Application.BackgroundJobs;

// The real WP-8 notification-delivery cycle - replaces NoOpBackgroundJobCycle as the one
// IBackgroundJobCycle BackgroundJobsWorker (BookSpace.Api) resolves from its per-cycle scope. See
// docs/background-jobs.md ("Notification outbox cycle") for the full runtime story: worker -> lease ->
// this cycle -> INotificationOutboxReader -> INotificationOutboxProcessor -> INotificationSender.
//
// This class is deliberately thin: it owns none of AttemptCount/backoff/AvailableAtUtc/Sent/DeadLetter/
// transient-vs-permanent classification/SMTP sending - INotificationOutboxProcessor already owns every
// bit of that, unchanged. The only things this cycle adds are orchestration (one bounded due-batch query
// per poll, handed to the processor in one call), the Email:Enabled short-circuit, job-run correlation,
// and the one summary log line a cycle emits when it completes normally.
//
// Safe to register Scoped as before: every dependency here (the reader, the processor, IOptions<T>,
// IBackgroundJobInstanceIdentity, ICorrelationIdContext, TimeProvider, ILogger) is itself either Scoped
// or Singleton-safe, and this type is only ever resolved from the fresh per-cycle scope
// BackgroundJobsWorker already creates - it is never held by that singleton worker itself.
public sealed class NotificationOutboxJobCycle(
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

        if (!emailOptions.Value.Enabled)
        {
            // Skips the due-batch query entirely - not just the processor/sender - so a disabled
            // deployment never reads, never writes, never opens a network connection, and leaves every
            // Pending item completely untouched (AttemptCount unchanged, nothing dead-lettered). Relying
            // on SmtpNotificationSender's own Enabled guard instead would still have let the processor
            // mark real due items DeadLettered the first time this cycle ever ran with items present -
            // see docs/background-jobs.md ("Email disabled behavior").
            logger.LogInformation("Notification outbox cycle skipped - Email:Enabled is false.");
            return;
        }

        var batchSize = jobOptions.Value.BatchSize;
        var startTimestamp = timeProvider.GetTimestamp();

        // A second line (besides the end-of-cycle summary) carrying the same CorrelationId/JobName/
        // JobOwnerId scope - lets the run/item correlation chain (docs/background-jobs.md) actually be
        // followed from "a cycle started" through to "it finished" in a log stream, and gives tooling two
        // real points to confirm share one id, not just one.
        logger.LogDebug("Notification outbox cycle started. RequestedBatchSize={RequestedBatchSize}", batchSize);

        cancellationToken.ThrowIfCancellationRequested();

        var batch = await reader.GetDueBatchAsync(batchSize, cancellationToken);

        if (batch.Count == 0)
        {
            LogSummary(correlationId, batchSize, pickedCount: 0, results: [], timeProvider.GetElapsedTime(startTimestamp));
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

        LogSummary(correlationId, batchSize, batch.Count, results, timeProvider.GetElapsedTime(startTimestamp));
    }

    // Exactly one structured line per normally-completed cycle - every count below comes from the
    // structured NotificationOutboxBatchItemResult list the processor returned, never from parsing a log
    // message or an exception's text. See docs/background-jobs.md ("Notification outbox cycle") for why
    // PermanentFailureCount and DeadLetterCount are reported as the same number in this implementation:
    // NotificationOutboxItemOutcome does not distinguish "a permanent send failure" from "a transient
    // failure that just exhausted MaxNotificationAttempts" - both already collapse into the single
    // DeadLettered outcome inside NotificationOutboxProcessor, and splitting that further here would mean
    // reinterpreting/duplicating classification logic the processor already owns, not just reporting it.
    private void LogSummary(
        string correlationId,
        int requestedBatchSize,
        int pickedCount,
        IReadOnlyList<NotificationOutboxBatchItemResult> results,
        TimeSpan elapsed)
    {
        var succeededCount = results.Count(result => result.Outcome == NotificationOutboxItemOutcome.Sent);
        var transientFailureCount = results.Count(result => result.Outcome == NotificationOutboxItemOutcome.RetryScheduled);
        var deadLetterCount = results.Count(result => result.Outcome == NotificationOutboxItemOutcome.DeadLettered);
        var unexpectedFailureCount = results.Count(result => result.Outcome is null);

        logger.LogInformation(
            "Notification outbox cycle completed. JobName={JobName} CorrelationId={CorrelationId} "
            + "RequestedBatchSize={RequestedBatchSize} PickedCount={PickedCount} SucceededCount={SucceededCount} "
            + "TransientFailureCount={TransientFailureCount} PermanentFailureCount={PermanentFailureCount} "
            + "DeadLetterCount={DeadLetterCount} UnexpectedFailureCount={UnexpectedFailureCount} ElapsedMilliseconds={ElapsedMilliseconds}",
            JobName,
            correlationId,
            requestedBatchSize,
            pickedCount,
            succeededCount,
            transientFailureCount,
            deadLetterCount, // PermanentFailureCount - see the doc comment above for why this equals DeadLetterCount here
            deadLetterCount,
            unexpectedFailureCount,
            elapsed.TotalMilliseconds);
    }
}
