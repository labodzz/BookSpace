using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves NotificationOutboxReader.GetDueBatchAsync's due-selection, ordering, batch-bounding, and
// cross-tenant read behavior against a REAL SQL Server (LocalDB) - see docs/background-jobs.md
// ("Due-batch query"). The "server-side TOP, never materialize-then-filter" and index-usage claims in
// particular can only be proven here, not against SQLite or a mock.
public sealed class NotificationOutboxReaderTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceNotificationOutboxReaderTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantAId;
    private Guid _tenantBId;
    private Guid _userAId;
    private Guid _userBId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureCreatedAsync();

        _tenantAId = Guid.NewGuid();
        _tenantBId = Guid.NewGuid();
        _userAId = Guid.NewGuid();
        _userBId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        dbContext.Tenants.Add(new Tenant { Id = _tenantAId, Name = "Reader Tenant A", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.Tenants.Add(new Tenant { Id = _tenantBId, Name = "Reader Tenant B", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.Users.Add(new User
        {
            Id = _userAId, TenantId = _tenantAId, FirstName = "Reader", LastName = "A",
            Email = $"reader-a-{Guid.NewGuid():N}@bookspace.test", PasswordHash = "x", CreatedAtUtc = now,
        });
        dbContext.Users.Add(new User
        {
            Id = _userBId, TenantId = _tenantBId, FirstName = "Reader", LastName = "B",
            Email = $"reader-b-{Guid.NewGuid():N}@bookspace.test", PasswordHash = "x", CreatedAtUtc = now,
        });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task GetDueBatchAsync_WithNoRowsAtAll_ReturnsAnEmptyList()
    {
        var result = await GetDueBatchAsync(batchSize: 50);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetDueBatchAsync_WithADuePendingItem_ReturnsIt()
    {
        var itemId = await SeedItemAsync(_tenantAId, _userAId, availableAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));

        var result = await GetDueBatchAsync(batchSize: 50);

        Assert.Single(result);
        Assert.Equal(itemId, result[0].Id);
    }

    [Fact]
    public async Task GetDueBatchAsync_WithAFutureItem_DoesNotReturnIt()
    {
        await SeedItemAsync(_tenantAId, _userAId, availableAtUtc: DateTimeOffset.UtcNow.AddHours(1));

        var result = await GetDueBatchAsync(batchSize: 50);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetDueBatchAsync_WithAnAlreadySentItem_DoesNotReturnIt()
    {
        await SeedItemAsync(_tenantAId, _userAId, availableAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1), status: NotificationOutboxStatus.Sent);

        var result = await GetDueBatchAsync(batchSize: 50);

        Assert.Empty(result);
    }

    // DeadLettered (added by the per-item processor task) is the terminal-failure status this test class
    // previously had no coverage for - see the superseded comment this replaced, and
    // docs/background-jobs.md ("Dead-letter state"). A dead-lettered item must never be due again: it is
    // permanently done, successfully or not.
    [Fact]
    public async Task GetDueBatchAsync_WithADeadLetteredItem_DoesNotReturnIt()
    {
        await SeedItemAsync(_tenantAId, _userAId, availableAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1), status: NotificationOutboxStatus.DeadLettered);

        var result = await GetDueBatchAsync(batchSize: 50);

        Assert.Empty(result);
    }

    // NotificationOutboxStatus now has exactly three members: Pending, Sent, DeadLettered (see
    // BookSpace.Domain.Enums.NotificationOutboxStatus). This test exists so that whenever a FOURTH
    // terminal/non-due status is ever added, it fails here as a reminder to also add the corresponding
    // "this new status is never due" coverage, rather than that gap going unnoticed - exactly the
    // reminder this test's own predecessor was for DeadLettered.
    [Fact]
    public void NotificationOutboxStatus_HasExactlyTheStatusesThisTestClassAccountsFor()
    {
        Assert.Equal(["Pending", "Sent", "DeadLettered"], Enum.GetNames<NotificationOutboxStatus>());
    }

    [Fact]
    public async Task GetDueBatchAsync_WhenMoreItemsExistThanBatchSize_ReturnsExactlyBatchSizeItems()
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 7; i++)
        {
            await SeedItemAsync(_tenantAId, _userAId, availableAtUtc: now.AddMinutes(-7 + i));
        }

        var result = await GetDueBatchAsync(batchSize: 5);

        Assert.Equal(5, result.Count);
    }

    // Companion to the test above: not just "the count is right," but specifically that the EXCLUDED
    // items are the correct (newest) ones, not an arbitrary subset - proving Take(batchSize) is applied
    // after the due filter and ordering, not before.
    [Fact]
    public async Task GetDueBatchAsync_WhenMoreItemsExistThanBatchSize_ExcludesTheNewestItemsNotJustAnyExtras()
    {
        var now = DateTimeOffset.UtcNow;
        var ids = new List<Guid>();
        for (var i = 0; i < 7; i++)
        {
            // Oldest (i=0, AvailableAtUtc furthest in the past) to newest (i=6).
            ids.Add(await SeedItemAsync(_tenantAId, _userAId, availableAtUtc: now.AddMinutes(-7 + i)));
        }

        var result = await GetDueBatchAsync(batchSize: 5);

        Assert.Equal(ids.Take(5), result.Select(item => item.Id));
    }

    [Fact]
    public async Task GetDueBatchAsync_ReturnsItemsOldestAvailableAtUtcFirst()
    {
        var now = DateTimeOffset.UtcNow;
        var newer = await SeedItemAsync(_tenantAId, _userAId, availableAtUtc: now.AddMinutes(-1));
        var oldest = await SeedItemAsync(_tenantAId, _userAId, availableAtUtc: now.AddMinutes(-10));
        var middle = await SeedItemAsync(_tenantAId, _userAId, availableAtUtc: now.AddMinutes(-5));

        var result = await GetDueBatchAsync(batchSize: 50);

        Assert.Equal([oldest, middle, newer], result.Select(item => item.Id));
    }

    // Two items sharing the exact same AvailableAtUtc (a realistic case - e.g. a future reminder job
    // scheduling several bookings' reminders for the same computed instant) must still be ordered
    // deterministically, by CreatedAtUtc.
    [Fact]
    public async Task GetDueBatchAsync_WithATiedAvailableAtUtc_BreaksTheTieByCreatedAtUtc()
    {
        var sharedAvailableAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var createdLater = await SeedItemAsync(_tenantAId, _userAId, availableAtUtc: sharedAvailableAtUtc, createdAtUtc: DateTimeOffset.UtcNow.AddMinutes(-2));
        var createdEarlier = await SeedItemAsync(_tenantAId, _userAId, availableAtUtc: sharedAvailableAtUtc, createdAtUtc: DateTimeOffset.UtcNow.AddMinutes(-3));

        var result = await GetDueBatchAsync(batchSize: 50);

        Assert.Equal([createdEarlier, createdLater], result.Select(item => item.Id));
    }

    // The final tie-breaker (Id) must still produce a deterministic (repeatable), not merely
    // "some order," result even when AvailableAtUtc AND CreatedAtUtc are identical for every row - proven
    // by running the same query twice against unchanged data and requiring an identical sequence both
    // times, rather than asserting a specific absolute ordering (SQL Server's uniqueidentifier sort order
    // does not match .NET's Guid.CompareTo, so asserting a specific direction here would encode the wrong
    // assumption).
    [Fact]
    public async Task GetDueBatchAsync_WithFullyTiedAvailableAtUtcAndCreatedAtUtc_IsDeterministicAcrossRepeatedCalls()
    {
        var sharedInstant = DateTimeOffset.UtcNow.AddMinutes(-1);
        for (var i = 0; i < 6; i++)
        {
            await SeedItemAsync(_tenantAId, _userAId, availableAtUtc: sharedInstant, createdAtUtc: sharedInstant);
        }

        var firstCall = await GetDueBatchAsync(batchSize: 50);
        var secondCall = await GetDueBatchAsync(batchSize: 50);

        Assert.Equal(firstCall.Select(item => item.Id), secondCall.Select(item => item.Id));
    }

    // The core tenant requirement for this task: a system-wide background read must see due items across
    // EVERY tenant in one call, carrying each row's own TenantId so a future processor knows which
    // tenant's context to act in.
    [Fact]
    public async Task GetDueBatchAsync_ReturnsDueItemsFromMultipleTenantsInOneCall()
    {
        var itemA = await SeedItemAsync(_tenantAId, _userAId, availableAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));
        var itemB = await SeedItemAsync(_tenantBId, _userBId, availableAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));

        var result = await GetDueBatchAsync(batchSize: 50);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, item => item.Id == itemA && item.TenantId == _tenantAId);
        Assert.Contains(result, item => item.Id == itemB && item.TenantId == _tenantBId);
    }

    // The other half of the tenant requirement: GetDueBatchAsync's own IgnoreQueryFilters() must not leak
    // into - or otherwise be evidence of a change to - how an ORDINARY, tenant-scoped query behaves. A
    // plain query through a DbContext with a real ICurrentUserContext.TenantId must still only see that
    // tenant's own rows, exactly as docs/tenant-isolation.md describes, completely unaffected by this
    // task's reader existing.
    [Fact]
    public async Task OrdinaryTenantScopedQuery_StillOnlySeesItsOwnTenantsItems_UnaffectedByTheDueBatchReader()
    {
        await SeedItemAsync(_tenantAId, _userAId, availableAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));
        await SeedItemAsync(_tenantBId, _userBId, availableAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));

        await using var tenantScopedContext = CreateDbContext(_tenantAId);
        var visibleToTenantA = await tenantScopedContext.NotificationOutboxItems.ToListAsync();

        Assert.Single(visibleToTenantA);
        Assert.Equal(_tenantAId, visibleToTenantA[0].TenantId);
    }

    [Fact]
    public async Task GetDueBatchAsync_WithAnAlreadyCancelledToken_ThrowsWithoutReturning()
    {
        await SeedItemAsync(_tenantAId, _userAId, availableAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));

        await using var dbContext = CreateDbContext();
        var reader = new NotificationOutboxReader(dbContext, NullLogger<NotificationOutboxReader>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.GetDueBatchAsync(50, new CancellationToken(canceled: true)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GetDueBatchAsync_WithAZeroOrNegativeBatchSize_ThrowsArgumentOutOfRangeException(int invalidBatchSize)
    {
        await using var dbContext = CreateDbContext();
        var reader = new NotificationOutboxReader(dbContext, NullLogger<NotificationOutboxReader>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.GetDueBatchAsync(invalidBatchSize, CancellationToken.None));
    }

    // Proves the query is genuinely server-side and batch-bounded in the SQL itself (SELECT TOP(@p)), not
    // "fetch everything, then Take in memory" - captured from EF's own command logging against the real
    // provider, not asserted from reading the implementation's source.
    [Fact]
    public async Task GetDueBatchAsync_GeneratesASqlTopLimitedQuery_RatherThanMaterializingEverythingFirst()
    {
        await SeedItemAsync(_tenantAId, _userAId, availableAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));

        var capturedCommands = new List<string>();
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>()
            .UseSqlServer(_connectionString)
            .LogTo(message => capturedCommands.Add(message), [DbLoggerCategory.Database.Command.Name], LogLevel.Information)
            .Options;
        await using var dbContext = new BookSpaceDbContext(options, new FixedCurrentUserContext());
        var reader = new NotificationOutboxReader(dbContext, NullLogger<NotificationOutboxReader>.Instance);

        await reader.GetDueBatchAsync(50, CancellationToken.None);

        Assert.Contains(capturedCommands, message => message.Contains("SELECT TOP(", StringComparison.Ordinal));
    }

    private async Task<IReadOnlyList<BookSpace.Application.Notifications.DueNotificationOutboxItem>> GetDueBatchAsync(int batchSize)
    {
        await using var dbContext = CreateDbContext();
        var reader = new NotificationOutboxReader(dbContext, NullLogger<NotificationOutboxReader>.Instance);
        return await reader.GetDueBatchAsync(batchSize, CancellationToken.None);
    }

    private async Task<Guid> SeedItemAsync(
        Guid tenantId, Guid recipientUserId, DateTimeOffset availableAtUtc, DateTimeOffset? createdAtUtc = null, NotificationOutboxStatus status = NotificationOutboxStatus.Pending)
    {
        var id = Guid.NewGuid();
        await using var dbContext = CreateDbContext();
        dbContext.NotificationOutboxItems.Add(new NotificationOutboxItem
        {
            Id = id,
            TenantId = tenantId,
            NotificationType = "Booking.Confirmation",
            RecipientUserId = recipientUserId,
            PayloadJson = "{}",
            IdempotencyKey = $"test:{id:N}",
            CreatedAtUtc = createdAtUtc ?? availableAtUtc,
            AvailableAtUtc = availableAtUtc,
            Status = status,
            AttemptCount = 0,
        });
        await dbContext.SaveChangesAsync();
        return id;
    }

    private BookSpaceDbContext CreateDbContext(Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString).Options;
        return new BookSpaceDbContext(options, new FixedCurrentUserContext(tenantId));
    }

    // Default TenantId => null, mirroring the system-wide background context GetDueBatchAsync actually
    // runs under - see docs/tenant-isolation.md for why that fails closed rather than open for an
    // ordinary (non-IgnoreQueryFilters) query, which is exactly why GetDueBatchAsync needs its own
    // explicit exception. A real TenantId is only passed for the ordinary-tenant-scoped-query test above.
    private sealed class FixedCurrentUserContext(Guid? tenantId = null) : ICurrentUserContext
    {
        public Guid? UserId => null;
        public Guid? TenantId => tenantId;
        public IReadOnlyCollection<string> Roles => [];
    }
}
