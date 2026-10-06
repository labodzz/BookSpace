using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves CalendarSubscriptionRepository's tenant-filter behavior (ambient for FindActiveByUserAsync,
// explicitly bypassed for FindActiveByTokenHashAsync) and the filtered-unique-index "at most one ACTIVE
// subscription per user" guarantee against a REAL SQL Server (LocalDB) - the unique-index enforcement in
// particular can only be proven here, not against a mock. See docs/calendar-subscription-feed.md.
public sealed class CalendarSubscriptionRepositoryTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceCalendarSubscriptionRepositoryTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantId;
    private Guid _userId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureCreatedAsync();

        _tenantId = Guid.NewGuid();
        _userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        dbContext.Tenants.Add(new Tenant { Id = _tenantId, Name = "Calendar Sub Tenant", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.Users.Add(new User { Id = _userId, TenantId = _tenantId, FirstName = "Cal", LastName = "Sub", Email = $"cal-sub-{Guid.NewGuid():N}@bookspace.test", PasswordHash = "x", CreatedAtUtc = now });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task FindActiveByUserAsync_WithAnActiveSubscription_ReturnsIt()
    {
        var subscriptionId = await SeedAsync(_tenantId, _userId, "hash-1");

        await using var dbContext = CreateDbContext(_tenantId);
        var repository = new CalendarSubscriptionRepository(dbContext);

        var result = await repository.FindActiveByUserAsync(_userId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(subscriptionId, result!.Id);
    }

    [Fact]
    public async Task FindActiveByUserAsync_WithOnlyARevokedSubscription_ReturnsNull()
    {
        await SeedAsync(_tenantId, _userId, "hash-revoked", revokedAtUtc: DateTimeOffset.UtcNow);

        await using var dbContext = CreateDbContext(_tenantId);
        var repository = new CalendarSubscriptionRepository(dbContext);

        var result = await repository.FindActiveByUserAsync(_userId, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FindActiveByUserAsync_WithNoSubscriptionAtAll_ReturnsNull()
    {
        await using var dbContext = CreateDbContext(_tenantId);
        var repository = new CalendarSubscriptionRepository(dbContext);

        var result = await repository.FindActiveByUserAsync(_userId, CancellationToken.None);

        Assert.Null(result);
    }

    // The anonymous feed request has NO ambient tenant context at all (unlike FindActiveByUserAsync's
    // callers, which always run authenticated) - proves the lookup still succeeds with the ambient
    // TenantId left completely unset (null), the same shape GetCalendarFeedQueryHandler's real caller has.
    [Fact]
    public async Task FindActiveByTokenHashAsync_WithNoAmbientTenantContextAtAll_StillFindsTheActiveRow()
    {
        var subscriptionId = await SeedAsync(_tenantId, _userId, "hash-anon-lookup");

        await using var dbContext = CreateDbContext(tenantId: null);
        var repository = new CalendarSubscriptionRepository(dbContext);

        var result = await repository.FindActiveByTokenHashAsync("hash-anon-lookup", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(subscriptionId, result!.Id);
    }

    [Fact]
    public async Task FindActiveByTokenHashAsync_WithARevokedToken_ReturnsNull()
    {
        await SeedAsync(_tenantId, _userId, "hash-revoked-lookup", revokedAtUtc: DateTimeOffset.UtcNow);

        await using var dbContext = CreateDbContext(tenantId: null);
        var repository = new CalendarSubscriptionRepository(dbContext);

        var result = await repository.FindActiveByTokenHashAsync("hash-revoked-lookup", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FindActiveByTokenHashAsync_WithAnUnknownHash_ReturnsNull()
    {
        await using var dbContext = CreateDbContext(tenantId: null);
        var repository = new CalendarSubscriptionRepository(dbContext);

        var result = await repository.FindActiveByTokenHashAsync("never-issued-hash", CancellationToken.None);

        Assert.Null(result);
    }

    // The real backstop behind Regenerate's "revoke then insert" sequence: even if two concurrent
    // regenerations both revoked the same prior row and both tried to insert a fresh active one, only one
    // INSERT can ever succeed - the filtered unique index on UserId (WHERE RevokedAtUtc IS NULL) rejects
    // the second as an ordinary unique-constraint violation.
    [Fact]
    public async Task AddingASecondActiveSubscriptionForTheSameUser_ViolatesTheFilteredUniqueIndex()
    {
        await SeedAsync(_tenantId, _userId, "hash-first-active");

        await using var dbContext = CreateDbContext(_tenantId);
        dbContext.CalendarSubscriptions.Add(new CalendarSubscription
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, UserId = _userId, TokenHash = "hash-second-active", CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    // The filter is on RevokedAtUtc, not UserId alone - a revoked prior row must never block a fresh one.
    [Fact]
    public async Task AddingAnActiveSubscription_ForAUserWhoseOnlyPriorSubscriptionIsRevoked_Succeeds()
    {
        await SeedAsync(_tenantId, _userId, "hash-old-revoked", revokedAtUtc: DateTimeOffset.UtcNow);

        await using var dbContext = CreateDbContext(_tenantId);
        dbContext.CalendarSubscriptions.Add(new CalendarSubscription
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, UserId = _userId, TokenHash = "hash-new-active", CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        await dbContext.SaveChangesAsync(); // does not throw
    }

    private async Task<Guid> SeedAsync(Guid tenantId, Guid userId, string tokenHash, DateTimeOffset? revokedAtUtc = null)
    {
        var id = Guid.NewGuid();
        await using var dbContext = CreateDbContext();
        dbContext.CalendarSubscriptions.Add(new CalendarSubscription
        {
            Id = id, TenantId = tenantId, UserId = userId, TokenHash = tokenHash, CreatedAtUtc = DateTimeOffset.UtcNow, RevokedAtUtc = revokedAtUtc,
        });
        await dbContext.SaveChangesAsync();
        return id;
    }

    private BookSpaceDbContext CreateDbContext(Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString).Options;
        return new BookSpaceDbContext(options, new FixedCurrentUserContext(tenantId));
    }

    private sealed class FixedCurrentUserContext(Guid? tenantId = null) : ICurrentUserContext
    {
        public Guid? UserId => null;
        public Guid? TenantId => tenantId;
        public IReadOnlyCollection<string> Roles => [];
    }
}
