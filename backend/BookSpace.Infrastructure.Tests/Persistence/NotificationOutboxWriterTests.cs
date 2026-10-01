using BookSpace.Application.Notifications;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves NotificationOutboxWriter's idempotent-insert protocol against a REAL SQL Server (LocalDB) - see
// docs/background-jobs.md ("Notification outbox"). The concurrent-enqueue race in particular can only be
// proven against a real unique index, not SQLite or a mock.
public sealed class NotificationOutboxWriterTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceNotificationOutboxTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantAId;
    private Guid _tenantBId;
    private Guid _userId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureCreatedAsync();

        _tenantAId = Guid.NewGuid();
        _tenantBId = Guid.NewGuid();
        _userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        dbContext.Tenants.Add(new Tenant { Id = _tenantAId, Name = "Outbox Tenant A", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.Tenants.Add(new Tenant { Id = _tenantBId, Name = "Outbox Tenant B", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.Users.Add(new User
        {
            Id = _userId, TenantId = _tenantAId, FirstName = "Outbox", LastName = "User",
            Email = $"outbox-{Guid.NewGuid():N}@bookspace.test", PasswordHash = "x", CreatedAtUtc = now,
        });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task EnqueueAsync_FirstTimeForAKey_CreatesOneRowInThePendingState()
    {
        var result = await EnqueueAsync(BuildRequest("booking:1:confirmation"));

        Assert.Equal(EnqueueOutcome.Created, result.Outcome);
        Assert.NotNull(result.OutboxItemId);

        await using var verify = CreateDbContext();
        var item = await verify.NotificationOutboxItems.IgnoreQueryFilters().SingleAsync(i => i.Id == result.OutboxItemId);
        Assert.Equal(NotificationOutboxStatus.Pending, item.Status);
        Assert.Equal(0, item.AttemptCount);
        Assert.Null(item.LastAttemptAtUtc);
    }

    [Fact]
    public async Task EnqueueAsync_CalledTwiceWithTheSameKey_DoesNotCreateASecondRow()
    {
        var request = BuildRequest("booking:2:confirmation");

        var first = await EnqueueAsync(request);
        var second = await EnqueueAsync(request);

        Assert.Equal(EnqueueOutcome.Created, first.Outcome);
        Assert.Equal(EnqueueOutcome.AlreadyExists, second.Outcome);
        Assert.Null(second.OutboxItemId);

        await using var verify = CreateDbContext();
        Assert.Equal(1, await verify.NotificationOutboxItems.IgnoreQueryFilters().CountAsync(i => i.IdempotencyKey == "booking:2:confirmation"));
    }

    // The core concurrency guarantee: two callers racing to enqueue the SAME logical notification must
    // leave exactly one row, never two (overlapping) and never a crash instead of a clean "already
    // exists" loser. The Barrier forces both attempts to genuinely overlap rather than merely both being
    // scheduled - each gets its own BookSpaceDbContext, exactly like two real concurrent requests would.
    [Fact]
    public async Task EnqueueAsync_TwoConcurrentAttemptsWithTheSameKey_LeavesExactlyOneRow()
    {
        const string key = "booking:3:confirmation";
        using var startGate = new Barrier(2);

        async Task<EnqueueOutcome> AttemptAsync()
        {
            startGate.SignalAndWait();
            var result = await EnqueueAsync(BuildRequest(key));
            return result.Outcome;
        }

        var results = await Task.WhenAll(Task.Run(AttemptAsync), Task.Run(AttemptAsync));

        Assert.Equal(1, results.Count(outcome => outcome == EnqueueOutcome.Created));
        Assert.Equal(1, results.Count(outcome => outcome == EnqueueOutcome.AlreadyExists));

        await using var verify = CreateDbContext();
        Assert.Equal(1, await verify.NotificationOutboxItems.IgnoreQueryFilters().CountAsync(i => i.IdempotencyKey == key));
    }

    [Fact]
    public async Task EnqueueAsync_WithDifferentKeys_CreatesSeparateRows()
    {
        var first = await EnqueueAsync(BuildRequest("booking:4:confirmation"));
        var second = await EnqueueAsync(BuildRequest("booking:4:cancellation:1"));

        Assert.Equal(EnqueueOutcome.Created, first.Outcome);
        Assert.Equal(EnqueueOutcome.Created, second.Outcome);
        Assert.NotEqual(first.OutboxItemId, second.OutboxItemId);
    }

    // Proves the chosen (TenantId, IdempotencyKey) composite uniqueness, not a globally-unique
    // IdempotencyKey column: the identical key string under a DIFFERENT tenant must succeed
    // independently, never reported as a duplicate of the other tenant's row.
    [Fact]
    public async Task EnqueueAsync_SameKeyUnderADifferentTenant_CreatesASeparateRow()
    {
        const string key = "booking:5:confirmation";

        var first = await EnqueueAsync(BuildRequest(key, tenantId: _tenantAId));
        var second = await EnqueueAsync(BuildRequest(key, tenantId: _tenantBId));

        Assert.Equal(EnqueueOutcome.Created, first.Outcome);
        Assert.Equal(EnqueueOutcome.Created, second.Outcome); // not AlreadyExists - different tenant
        Assert.NotEqual(first.OutboxItemId, second.OutboxItemId);

        await using var verify = CreateDbContext();
        Assert.Equal(2, await verify.NotificationOutboxItems.IgnoreQueryFilters().CountAsync(i => i.IdempotencyKey == key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EnqueueAsync_WithAnEmptyOrWhitespaceKey_ThrowsArgumentException(string invalidKey)
    {
        await using var dbContext = CreateDbContext();
        var writer = new NotificationOutboxWriter(dbContext, NullLogger<NotificationOutboxWriter>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => writer.EnqueueAsync(BuildRequest(invalidKey), CancellationToken.None));
    }

    [Fact]
    public async Task EnqueueAsync_WithAKeyLongerThanTheMaximum_ThrowsArgumentException()
    {
        await using var dbContext = CreateDbContext();
        var writer = new NotificationOutboxWriter(dbContext, NullLogger<NotificationOutboxWriter>.Instance);
        var tooLong = new string('a', NotificationOutboxItem.MaxIdempotencyKeyLength + 1);

        await Assert.ThrowsAsync<ArgumentException>(() => writer.EnqueueAsync(BuildRequest(tooLong), CancellationToken.None));
    }

    [Fact]
    public async Task EnqueueAsync_WithAKeyExactlyAtTheMaximumLength_Succeeds()
    {
        var maxLengthKey = new string('a', NotificationOutboxItem.MaxIdempotencyKeyLength);

        var result = await EnqueueAsync(BuildRequest(maxLengthKey));

        Assert.Equal(EnqueueOutcome.Created, result.Outcome);
    }

    [Fact]
    public async Task EnqueueAsync_StoresCreatedAndAvailableInstantsAsUtc()
    {
        var beforeUtc = DateTimeOffset.UtcNow;

        var result = await EnqueueAsync(BuildRequest("booking:6:confirmation"));

        var afterUtc = DateTimeOffset.UtcNow;
        await using var verify = CreateDbContext();
        var item = await verify.NotificationOutboxItems.IgnoreQueryFilters().SingleAsync(i => i.Id == result.OutboxItemId);

        Assert.Equal(TimeSpan.Zero, item.CreatedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, item.AvailableAtUtc.Offset);
        Assert.InRange(item.CreatedAtUtc, beforeUtc.AddSeconds(-2), afterUtc.AddSeconds(2));
        Assert.Equal(item.CreatedAtUtc, item.AvailableAtUtc); // no explicit AvailableAtUtc => immediately due
    }

    // An unrelated constraint violation (here, a RecipientUserId with no matching User row, violating the
    // foreign key) must propagate as-is, not be swallowed/misreported as "already exists" - only the
    // specific duplicate-key SQL errors (2601/2627) are ever treated as a benign repeat enqueue.
    [Fact]
    public async Task EnqueueAsync_WhenAnUnrelatedConstraintIsViolated_PropagatesTheRealExceptionRatherThanReportingADuplicate()
    {
        await using var dbContext = CreateDbContext();
        var writer = new NotificationOutboxWriter(dbContext, NullLogger<NotificationOutboxWriter>.Instance);
        var request = BuildRequest("booking:7:confirmation") with { RecipientUserId = Guid.NewGuid() }; // no such User row

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => writer.EnqueueAsync(request, CancellationToken.None));

        var sqlException = Assert.IsType<SqlException>(exception.InnerException);
        Assert.NotEqual(2601, sqlException.Number);
        Assert.NotEqual(2627, sqlException.Number);
    }

    // After the expected duplicate-key failure, the SAME DbContext must still be usable for a further,
    // unrelated save - proving the failed entity was detached from the change tracker, not left dangling
    // to corrupt (or silently re-attempt) the next SaveChangesAsync on this scope.
    [Fact]
    public async Task EnqueueAsync_AfterADuplicateKeyConflict_LeavesTheChangeTrackerUsableForFurtherWrites()
    {
        await using var dbContext = CreateDbContext();
        var writer = new NotificationOutboxWriter(dbContext, NullLogger<NotificationOutboxWriter>.Instance);
        const string key = "booking:8:confirmation";

        await writer.EnqueueAsync(BuildRequest(key), CancellationToken.None);
        var duplicateResult = await writer.EnqueueAsync(BuildRequest(key), CancellationToken.None);
        Assert.Equal(EnqueueOutcome.AlreadyExists, duplicateResult.Outcome);

        var nextResult = await writer.EnqueueAsync(BuildRequest("booking:9:confirmation"), CancellationToken.None);
        Assert.Equal(EnqueueOutcome.Created, nextResult.Outcome);
    }

    // Functional proof that the schema/filtered index actually supports the future retry processor's
    // query shape (docs/background-jobs.md): Pending items whose AvailableAtUtc has passed, oldest first,
    // excluding both not-yet-due and already-Sent items. No repository for this exists yet (out of scope
    // for this task) - this exercises the query shape directly against the DbContext.
    [Fact]
    public async Task DueItemsQueryShape_ReturnsOnlyPendingItemsAvailableNowOrEarlier_OrderedOldestFirst()
    {
        var now = DateTimeOffset.UtcNow;

        await EnqueueAsync(BuildRequest("due:earliest", availableAtUtc: now.AddMinutes(-10)));
        await EnqueueAsync(BuildRequest("due:later-but-still-past", availableAtUtc: now.AddMinutes(-5)));
        await EnqueueAsync(BuildRequest("due:not-yet", availableAtUtc: now.AddMinutes(10)));
        var alreadySent = await EnqueueAsync(BuildRequest("due:already-sent", availableAtUtc: now.AddMinutes(-20)));

        // Simulates a future retry processor having already marked this one Sent - raw SQL since nothing
        // in this task's scope ever transitions Status away from Pending.
        await using (var mutateContext = CreateDbContext())
        {
            await mutateContext.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE NotificationOutboxItems SET Status = 'Sent' WHERE Id = {alreadySent.OutboxItemId}");
        }

        await using var verify = CreateDbContext();
        var dueKeys = await verify.NotificationOutboxItems
            .IgnoreQueryFilters()
            .Where(i => i.Status == NotificationOutboxStatus.Pending && i.AvailableAtUtc <= now)
            .OrderBy(i => i.AvailableAtUtc)
            .Select(i => i.IdempotencyKey)
            .ToListAsync();

        Assert.Equal(["due:earliest", "due:later-but-still-past"], dueKeys);
    }

    // Each call gets its own BookSpaceDbContext, exactly like two real concurrent requests would never
    // share one - the concurrency guarantee above depends on that being true.
    private async Task<EnqueueNotificationResult> EnqueueAsync(NotificationOutboxRequest request)
    {
        await using var dbContext = CreateDbContext();
        var writer = new NotificationOutboxWriter(dbContext, NullLogger<NotificationOutboxWriter>.Instance);
        return await writer.EnqueueAsync(request, CancellationToken.None);
    }

    private NotificationOutboxRequest BuildRequest(string idempotencyKey, Guid? tenantId = null, DateTimeOffset? availableAtUtc = null) =>
        new(tenantId ?? _tenantAId, "Booking.Confirmation", _userId, """{"bookingId":"test"}""", idempotencyKey, availableAtUtc);

    private BookSpaceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString).Options;
        return new BookSpaceDbContext(options, new FixedCurrentUserContext());
    }

    // NotificationOutboxItem is ITenantOwned but these tests always read/write through a context with no
    // ambient tenant - every query here either targets a single row by Id or deliberately spans both
    // seeded tenants (see EnqueueAsync_SameKeyUnderADifferentTenant_CreatesASeparateRow), so the global
    // tenant filter must not narrow any of them.
    private sealed class FixedCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId => null;
        public Guid? TenantId => null;
        public IReadOnlyCollection<string> Roles => [];
    }
}
