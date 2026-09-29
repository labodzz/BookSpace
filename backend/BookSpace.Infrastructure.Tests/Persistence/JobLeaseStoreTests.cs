using BookSpace.Application.Security;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves the atomic acquire/renew/release protocol backing the WP-8 job lease lock (see
// docs/background-jobs.md, "Job lease lock") against a REAL SQL Server (LocalDB) - in particular, the
// acquire race between two callers for the same still-missing row can only be proven against a real
// database's own primary-key enforcement, not SQLite or a mock. Every operation gets its own
// BookSpaceDbContext, exactly like two real concurrent poll cycles (possibly in different application
// instances) would never share one.
public sealed class JobLeaseStoreTests : IAsyncLifetime
{
    private const string JobName = "test-job";

    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceJobLeaseTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task TryAcquireAsync_ForAFreeJob_Succeeds()
    {
        var expiresAtUtc = await AcquireAsync("owner-a", TimeSpan.FromSeconds(60));

        Assert.NotNull(expiresAtUtc);
        Assert.True(expiresAtUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task TryAcquireAsync_WhenAnotherOwnerHoldsAnUnexpiredLease_Fails()
    {
        await AcquireAsync("owner-a", TimeSpan.FromSeconds(60));

        var result = await AcquireAsync("owner-b", TimeSpan.FromSeconds(60));

        Assert.Null(result);
    }

    [Fact]
    public async Task TryRenewAsync_ByTheCurrentOwner_ExtendsTheLease()
    {
        var initialExpiry = await AcquireAsync("owner-a", TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromSeconds(1));

        var renewedExpiry = await RenewAsync("owner-a", TimeSpan.FromSeconds(60));

        Assert.NotNull(renewedExpiry);
        Assert.True(renewedExpiry > initialExpiry);
    }

    [Fact]
    public async Task TryRenewAsync_ByADifferentOwner_Fails()
    {
        await AcquireAsync("owner-a", TimeSpan.FromSeconds(60));

        var result = await RenewAsync("owner-b", TimeSpan.FromSeconds(60));

        Assert.Null(result);
    }

    [Fact]
    public async Task TryReleaseAsync_ByADifferentOwner_LeavesTheLeaseIntact()
    {
        await AcquireAsync("owner-a", TimeSpan.FromSeconds(60));

        var released = await ReleaseAsync("owner-b");

        Assert.False(released);
        // owner-a still owns it and can still renew - the mismatched release above touched nothing.
        Assert.NotNull(await RenewAsync("owner-a", TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public async Task TryReleaseAsync_ByTheOwner_FreesTheJobForAnotherOwner()
    {
        await AcquireAsync("owner-a", TimeSpan.FromSeconds(60));

        var released = await ReleaseAsync("owner-a");

        Assert.True(released);
        Assert.NotNull(await AcquireAsync("owner-b", TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public async Task TryAcquireAsync_AfterTheLeaseHasExpired_AllowsAnotherOwnerToTakeOver()
    {
        await AcquireAsync("owner-a", TimeSpan.FromSeconds(1));

        await Task.Delay(TimeSpan.FromSeconds(2));

        var takeover = await AcquireAsync("owner-b", TimeSpan.FromSeconds(60));
        Assert.NotNull(takeover);

        // owner-a's lease was taken over - it can no longer renew what it used to hold.
        Assert.Null(await RenewAsync("owner-a", TimeSpan.FromSeconds(60)));
    }

    // The core concurrency guarantee: several callers racing to acquire the SAME still-missing job row
    // must result in exactly one winner, never both/all (overlapping ownership) and never a crash
    // instead of a clean loser. More than two callers (rather than exactly two) raises the odds that at
    // least two of them genuinely overlap inside JobLeaseStore's UPDATE-then-INSERT window, which is what
    // exercises its unique-constraint-violation handling, not just the ordinary "arrived a moment later
    // and saw a row already there" path.
    [Fact]
    public async Task TryAcquireAsync_SeveralConcurrentAttemptsForTheSameFreeJob_ExactlyOneWins()
    {
        const int concurrentAttempts = 8;
        using var startGate = new Barrier(concurrentAttempts);

        async Task<DateTimeOffset?> AttemptAsync(int ownerIndex)
        {
            startGate.SignalAndWait();
            return await AcquireAsync($"owner-{ownerIndex}", TimeSpan.FromSeconds(60));
        }

        var results = await Task.WhenAll(Enumerable.Range(0, concurrentAttempts).Select(i => Task.Run(() => AttemptAsync(i))));

        Assert.Equal(1, results.Count(r => r is not null));
        Assert.Equal(concurrentAttempts - 1, results.Count(r => r is null));

        await using var verifyContext = CreateDbContext();
        Assert.Equal(1, await verifyContext.JobLeases.CountAsync(lease => lease.JobName == JobName));
    }

    private async Task<DateTimeOffset?> AcquireAsync(string ownerId, TimeSpan leaseDuration)
    {
        await using var dbContext = CreateDbContext();
        var store = new JobLeaseStore(dbContext);
        return await store.TryAcquireAsync(JobName, ownerId, leaseDuration, CancellationToken.None);
    }

    private async Task<DateTimeOffset?> RenewAsync(string ownerId, TimeSpan leaseDuration)
    {
        await using var dbContext = CreateDbContext();
        var store = new JobLeaseStore(dbContext);
        return await store.TryRenewAsync(JobName, ownerId, leaseDuration, CancellationToken.None);
    }

    private async Task<bool> ReleaseAsync(string ownerId)
    {
        await using var dbContext = CreateDbContext();
        var store = new JobLeaseStore(dbContext);
        return await store.TryReleaseAsync(JobName, ownerId, CancellationToken.None);
    }

    private BookSpaceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString).Options;
        return new BookSpaceDbContext(options, new FixedCurrentUserContext());
    }

    // JobLease is not ITenantOwned, so no tenant context is ever needed to read/write it - this stub
    // exists only because BookSpaceDbContext's constructor requires an ICurrentUserContext.
    private sealed class FixedCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId => null;
        public Guid? TenantId => null;
        public IReadOnlyCollection<string> Roles => [];
    }
}
