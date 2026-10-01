using BookSpace.Application.Notifications;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BookSpace.Infrastructure.Persistence;

// Idempotent outbox insert - see docs/background-jobs.md ("Notification outbox") for the full design.
//
// Deliberately NOT the usual AddAsync()-then-separate-SaveChangesAsync() repository split every other
// repository in this codebase uses: EnqueueAsync's whole point is to answer, immediately and
// definitively, "was a new row created or did one already exist for this key" - an answer that can only
// come from actually attempting the insert against the database's own unique constraint, not from
// deferring it to some later, caller-controlled SaveChanges. See the "Transaction boundary" section of
// docs/background-jobs.md for how this still composes with a future business write sharing the same
// scoped DbContext/transaction, despite calling SaveChangesAsync itself here.
internal sealed class NotificationOutboxWriter(BookSpaceDbContext dbContext, ILogger<NotificationOutboxWriter> logger) : INotificationOutboxWriter
{
    // Mirrors DbContextConcurrencyExtensions's constants - duplicated locally (not reused) because that
    // helper throws ConflictException on a duplicate, which is the right behavior for a client-facing
    // write conflict but wrong here: an idempotent repeat enqueue is a normal, expected outcome this
    // method must return a value for, never throw for.
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;

    public async Task<EnqueueNotificationResult> EnqueueAsync(NotificationOutboxRequest request, CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(request.IdempotencyKey);

        var now = DateTimeOffset.UtcNow;
        var item = new NotificationOutboxItem
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId,
            NotificationType = request.NotificationType,
            RecipientUserId = request.RecipientUserId,
            PayloadJson = request.PayloadJson,
            IdempotencyKey = request.IdempotencyKey,
            CreatedAtUtc = now,
            AvailableAtUtc = request.AvailableAtUtc ?? now,
            Status = NotificationOutboxStatus.Pending,
            AttemptCount = 0,
        };

        await dbContext.NotificationOutboxItems.AddAsync(item, cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException
        {
            Number: UniqueIndexViolation or UniqueConstraintViolation,
        } sqlException)
        {
            // The failed insert stays tracked as Added after a failed SaveChangesAsync - left alone, the
            // very next SaveChangesAsync on this same (shared, scoped) DbContext would try to re-insert
            // the identical row and fail identically, or worse, get bundled into an unrelated caller's
            // next commit. Detaching restores the tracker to exactly the state it was in before this
            // method ran.
            dbContext.Entry(item).State = EntityState.Detached;

            logger.LogInformation(
                "Notification outbox enqueue skipped - already recorded. {NotificationType} for tenant {TenantId} (SQL error {SqlErrorNumber}).",
                request.NotificationType,
                request.TenantId,
                sqlException.Number);
            return new EnqueueNotificationResult(EnqueueOutcome.AlreadyExists, OutboxItemId: null);
        }

        logger.LogInformation(
            "Notification outbox item created. {NotificationType} for tenant {TenantId}, available at {AvailableAtUtc}.",
            request.NotificationType,
            request.TenantId,
            item.AvailableAtUtc);
        return new EnqueueNotificationResult(EnqueueOutcome.Created, item.Id);
    }

    // A malformed key is a caller bug (a future handler building the key string incorrectly), not a
    // runtime race - it fails fast with ArgumentException rather than being reported through
    // EnqueueNotificationResult, which is reserved for the two legitimate idempotent outcomes.
    private static void ValidateIdempotencyKey(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException("IdempotencyKey must not be null, empty, or whitespace.", nameof(idempotencyKey));
        }

        if (idempotencyKey.Length > NotificationOutboxItem.MaxIdempotencyKeyLength)
        {
            throw new ArgumentException(
                $"IdempotencyKey must not exceed {NotificationOutboxItem.MaxIdempotencyKeyLength} characters.", nameof(idempotencyKey));
        }
    }
}
