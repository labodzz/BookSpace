using BookSpace.Application.Common;
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

    // The outbox's own idempotency index (BookSpaceModelConfiguration: HasIndex(TenantId, IdempotencyKey)
    // - EF Core's convention-generated name, confirmed against the AddNotificationOutbox migration).
    // Checked explicitly (not just the SQL error NUMBER) since a caller may stage OTHER entities with
    // their OWN unique constraints on the SAME DbContext before calling EnqueueAsync/EnqueueManyAsync as
    // its single commit point (see docs/background-jobs.md, "Notification outbox" - "A known, currently-
    // accepted limitation of this mechanism" - this is that limitation, closed): SQL Server's own 2601/
    // 2627 error message always names the specific object/index that actually violated
    // ("Cannot insert duplicate key row in object 'dbo.NotificationOutboxItems' with unique index
    // 'IX_NotificationOutboxItems_TenantId_IdempotencyKey'"), so matching on that name is what lets a
    // DIFFERENT entity's unique-constraint violation (e.g. a tenant-onboarding handler's own Tenant.Name
    // or User.Email conflict, staged earlier in the same SaveChangesAsync call) propagate as a genuine,
    // uncaught DbUpdateException instead of being silently misreported as "this notification already
    // exists". Every EXISTING caller's own SaveChangesAsync batch has only ever had the outbox's own
    // index capable of colliding at that instant, so this check is always true for them too - this
    // narrows what gets swallowed, it never widens it, and changes no existing caller's observed behavior.
    private const string IdempotencyIndexName = "IX_NotificationOutboxItems_TenantId_IdempotencyKey";

    public async Task<EnqueueNotificationResult> EnqueueAsync(NotificationOutboxRequest request, CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(request.IdempotencyKey);
        var item = BuildItem(request);
        await dbContext.NotificationOutboxItems.AddAsync(item, cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsDuplicateKeyViolation(exception))
        {
            // The failed insert stays tracked as Added after a failed SaveChangesAsync - left alone, the
            // very next SaveChangesAsync on this same (shared, scoped) DbContext would try to re-insert
            // the identical row and fail identically, or worse, get bundled into an unrelated caller's
            // next commit. Detaching restores the tracker to exactly the state it was in before this
            // method ran.
            dbContext.Entry(item).State = EntityState.Detached;
            LogAlreadyExists(request);
            return new EnqueueNotificationResult(EnqueueOutcome.AlreadyExists, OutboxItemId: null);
        }
        catch (DbUpdateException exception) when (IsOtherUniqueConstraintViolation(exception))
        {
            // Some OTHER entity staged on this same DbContext (e.g. a tenant-onboarding handler's own
            // Tenant.Name or User.Email unique constraint) violated at the exact instant this shared
            // SaveChangesAsync ran - not a repeat enqueue of this notification. Translated the same way
            // DbContextConcurrencyExtensions.SaveChangesHandlingConflictsAsync already translates the
            // identical two SQL error numbers for every ordinary repository, so a caller in
            // BookSpace.Application (which cannot reference Microsoft.Data.SqlClient/EntityFrameworkCore
            // itself) never needs to catch DbUpdateException/SqlException to detect this race.
            dbContext.Entry(item).State = EntityState.Detached;
            throw new ConflictException("The request conflicts with existing data.");
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // The "two updates collided" sibling of the catch above - some OTHER entity staged on this
            // same DbContext (e.g. ResendTenantAdminInvitationCommandHandler's own
            // previousActiveInvitation.RevokedAtUtc write) lost a RowVersion race against a concurrent
            // writer that modified the exact same row first. Mirrors
            // DbContextConcurrencyExtensions.SaveChangesHandlingConflictsAsync's identical handling of this
            // exact exception type for every ordinary repository - see docs/tenant-onboarding.md
            // ("Concurrent resend").
            dbContext.Entry(item).State = EntityState.Detached;
            logger.LogWarning(exception, "Optimistic concurrency conflict handled as a conflict");
            throw new ConflictException("The record was modified by another request. Reload and try again.");
        }

        LogCreated(request, item.AvailableAtUtc);
        return new EnqueueNotificationResult(EnqueueOutcome.Created, item.Id);
    }

    public async Task<IReadOnlyList<EnqueueNotificationResult>> EnqueueManyAsync(
        IReadOnlyList<NotificationOutboxRequest> requests, CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            // No-op by design: nothing to enqueue and no SaveChangesAsync call here means a caller with no
            // other staged change on this DbContext performs no write at all for an empty batch.
            return [];
        }

        foreach (var request in requests)
        {
            ValidateIdempotencyKey(request.IdempotencyKey);
        }

        var items = requests.Select(BuildItem).ToList();
        await dbContext.NotificationOutboxItems.AddRangeAsync(items, cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            for (var index = 0; index < requests.Count; index++)
            {
                LogCreated(requests[index], items[index].AvailableAtUtc);
            }

            return items.Select(item => new EnqueueNotificationResult(EnqueueOutcome.Created, item.Id)).ToList();
        }
        catch (DbUpdateException exception) when (IsDuplicateKeyViolation(exception))
        {
            // At least one key in this batch already exists - by far the most common cause is the WHOLE
            // caller operation (e.g. a cascaded approve/cancel) being retried after it had already fully
            // succeeded, so every key in the batch is expected to conflict, not just one. SQL Server does
            // not tell us through this exception which row(s) specifically violated the constraint when
            // several inserts are batched into one round trip, so every item added here is detached and
            // re-evaluated against the database directly below, rather than assumed.
            foreach (var item in items)
            {
                dbContext.Entry(item).State = EntityState.Detached;
            }

            return await RetryAfterRemovingAlreadyEnqueuedAsync(requests, items, cancellationToken);
        }
        catch (DbUpdateException exception) when (IsOtherUniqueConstraintViolation(exception))
        {
            foreach (var item in items)
            {
                dbContext.Entry(item).State = EntityState.Detached;
            }

            throw new ConflictException("The request conflicts with existing data.");
        }
        catch (DbUpdateConcurrencyException exception)
        {
            foreach (var item in items)
            {
                dbContext.Entry(item).State = EntityState.Detached;
            }

            logger.LogWarning(exception, "Optimistic concurrency conflict handled as a conflict");
            throw new ConflictException("The record was modified by another request. Reload and try again.");
        }
    }

    // Re-checks exactly which (TenantId, IdempotencyKey) pairs in this batch already exist (one SELECT),
    // then retries SaveChangesAsync with only the genuinely new rows added - this second SaveChangesAsync
    // still also carries whatever OTHER (non-outbox) entities the caller staged before calling
    // EnqueueManyAsync, since nothing committed on the first, failed attempt. If even this still races
    // against a concurrent writer inserting the exact same key in between (rare: this would be the second
    // independent race on the same key), falls back to the single-item EnqueueAsync path per remaining
    // item, which already proves this exact race safe under real concurrency.
    private async Task<IReadOnlyList<EnqueueNotificationResult>> RetryAfterRemovingAlreadyEnqueuedAsync(
        IReadOnlyList<NotificationOutboxRequest> requests, IReadOnlyList<NotificationOutboxItem> originalItems, CancellationToken cancellationToken)
    {
        var tenantIds = requests.Select(request => request.TenantId).Distinct().ToList();
        var keys = requests.Select(request => request.IdempotencyKey).Distinct().ToList();
        var alreadyExisting = (await dbContext.NotificationOutboxItems
                .IgnoreQueryFilters() // a batch may legitimately span several tenants; this check must see all of them, not just the ambient one.
                .Where(item => tenantIds.Contains(item.TenantId) && keys.Contains(item.IdempotencyKey))
                .Select(item => new { item.TenantId, item.IdempotencyKey })
                .ToListAsync(cancellationToken))
            .Select(row => (row.TenantId, row.IdempotencyKey))
            .ToHashSet();

        var results = new EnqueueNotificationResult?[requests.Count];
        var retryItems = new List<NotificationOutboxItem>();
        for (var index = 0; index < requests.Count; index++)
        {
            if (alreadyExisting.Contains((requests[index].TenantId, requests[index].IdempotencyKey)))
            {
                LogAlreadyExists(requests[index]);
                results[index] = new EnqueueNotificationResult(EnqueueOutcome.AlreadyExists, OutboxItemId: null);
            }
            else
            {
                retryItems.Add(originalItems[index]);
            }
        }

        if (retryItems.Count > 0)
        {
            await dbContext.NotificationOutboxItems.AddRangeAsync(retryItems, cancellationToken);
        }

        try
        {
            // Always reached even when retryItems is empty (every key in the batch was a genuine repeat) -
            // this is still the call that commits whatever OTHER business mutations the caller staged
            // before invoking EnqueueManyAsync, since the very first SaveChangesAsync attempt above never
            // committed anything at all.
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsDuplicateKeyViolation(exception))
        {
            foreach (var item in retryItems)
            {
                dbContext.Entry(item).State = EntityState.Detached;
            }

            for (var index = 0; index < requests.Count; index++)
            {
                if (results[index] is not null)
                {
                    continue;
                }

                results[index] = await EnqueueAsync(requests[index], cancellationToken);
            }

            return results!;
        }
        catch (DbUpdateException exception) when (IsOtherUniqueConstraintViolation(exception))
        {
            foreach (var item in retryItems)
            {
                dbContext.Entry(item).State = EntityState.Detached;
            }

            throw new ConflictException("The request conflicts with existing data.");
        }
        catch (DbUpdateConcurrencyException exception)
        {
            foreach (var item in retryItems)
            {
                dbContext.Entry(item).State = EntityState.Detached;
            }

            logger.LogWarning(exception, "Optimistic concurrency conflict handled as a conflict");
            throw new ConflictException("The record was modified by another request. Reload and try again.");
        }

        for (var index = 0; index < requests.Count; index++)
        {
            results[index] ??= new EnqueueNotificationResult(EnqueueOutcome.Created, originalItems[index].Id);
            if (results[index]!.Outcome == EnqueueOutcome.Created)
            {
                LogCreated(requests[index], originalItems[index].AvailableAtUtc);
            }
        }

        return results!;
    }

    private static NotificationOutboxItem BuildItem(NotificationOutboxRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        return new NotificationOutboxItem
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
    }

    private static bool IsDuplicateKeyViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: UniqueIndexViolation or UniqueConstraintViolation } sqlException
        && sqlException.Message.Contains(IdempotencyIndexName, StringComparison.Ordinal);

    // Reached only when IsDuplicateKeyViolation's catch clause above has already declined the exception
    // (catch clauses are tried in source order) - so by construction this is a 2601/2627 violation on some
    // index OTHER than the outbox's own. Translated to ConflictException rather than left to propagate as
    // a raw DbUpdateException/SqlException, since BookSpace.Application callers cannot reference either type.
    private static bool IsOtherUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: UniqueIndexViolation or UniqueConstraintViolation };

    private void LogAlreadyExists(NotificationOutboxRequest request) =>
        logger.LogInformation(
            "Notification outbox enqueue skipped - already recorded. {NotificationType} for tenant {TenantId}.",
            request.NotificationType,
            request.TenantId);

    private void LogCreated(NotificationOutboxRequest request, DateTimeOffset availableAtUtc) =>
        logger.LogInformation(
            "Notification outbox item created. {NotificationType} for tenant {TenantId}, available at {AvailableAtUtc}.",
            request.NotificationType,
            request.TenantId,
            availableAtUtc);

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
