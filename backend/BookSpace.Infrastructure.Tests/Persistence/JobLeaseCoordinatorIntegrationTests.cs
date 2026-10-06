using BookSpace.Application.BackgroundJobs;
using BookSpace.Application.Security;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// End-to-end proof of the heartbeat and release/takeover halves of docs/background-jobs.md's "Job lease
// lock" protocol, wiring the REAL JobLeaseCoordinator (BookSpace.Application) to the REAL
// JobLeaseStore against LocalDB. JobLeaseCoordinatorTests (BookSpace.Application.Tests) already proves
// the heartbeat loop's own scheduling logic against a mocked store with a FakeTimeProvider; this file
// is what proves that loop actually keeps a real database row alive, and that disposing it releases the
// row for a genuinely different owner to take. JobLeaseStoreTests separately proves the
// crash/never-renewed-again case (a lease simply expiring with nobody heartbeating it).
public sealed class JobLeaseCoordinatorIntegrationTests : IAsyncLifetime
{
    private const string JobName = "test-job";

    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceJobLeaseCoordinatorTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

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
    public async Task TryAcquireAsync_HeartbeatKeepsTheLeaseAliveAcrossSeveralLeaseDurations_ThenDisposeReleasesItForAnotherOwner()
    {
        // Root-caused a full-suite-only (never isolated) flake here: JobLeaseStore.TryRenewAsync's WHERE
        // clause only succeeds while `LeaseExpiresAtUtc > SYSUTCDATETIME()` (see JobLeaseStore.cs) - with
        // the previous 1s heartbeat against a 2s lease, a renewal only had ONE second of margin (leaseDuration
        // - heartbeatInterval) before the PREVIOUS lease's own expiry. That is exactly the documented bare
        // minimum (docs/background-jobs.md, "Heartbeat": "BackgroundJobsOptionsValidator enforces
        // HeartbeatIntervalSeconds < LeaseDurationSeconds... there is always at least one full heartbeat
        // interval of margin"), not the documented RECOMMENDED margin (the shipped 20s/60s defaults leave a
        // 3x margin specifically so "one occasional delayed heartbeat isn't itself a lease loss"). Running
        // this one test in isolation, that 1-second margin was never actually exercised - Task.Delay's
        // continuation and the renewal's own new-DbContext-scope/SQL round trip reliably completed in low
        // single-digit milliseconds with LocalDB otherwise idle. Under the FULL Infrastructure.Tests suite,
        // dozens of other LocalDB-backed tests in this same process (several themselves multi-threaded,
        // Barrier-synchronized races) compete for the SAME finite .NET ThreadPool and the SAME single LocalDB
        // server process; an occasional renewal's continuation sitting queued, or LocalDB itself being busy
        // servicing another test's connection, for just over that 1-second margin is enough for ONE renewal
        // to miss its own lease's expiry - genuinely losing the lease by the store's own authoritative
        // database-clock comparison, correctly reported as lost (JobLeaseStore/JobLeaseCoordinator are both
        // behaving exactly as documented here - this was never a lease-protocol bug, never a test-isolation
        // bug, and never a false positive worth masking by weakening the assertion). The fix is to give the
        // test the SAME proportional margin production's own defaults already recommend: a 3x ratio
        // (6s lease / 2s heartbeat = 4 full seconds of margin per renewal, instead of 1) comfortably absorbs
        // the scheduling jitter a fully-parallel test run introduces, while still keeping this test's own
        // runtime short.
        var coordinatorA = CreateCoordinator("owner-a", leaseDurationSeconds: 6, heartbeatIntervalSeconds: 2);

        await using (var lease = await coordinatorA.TryAcquireAsync(JobName, CancellationToken.None))
        {
            Assert.NotNull(lease);

            // Longer than one lease duration - only survives without the lease being stolen if the
            // heartbeat actually renewed it at least once in the meantime.
            await Task.Delay(TimeSpan.FromSeconds(8));

            Assert.False(lease!.LeaseLostToken.IsCancellationRequested);

            var competingCoordinator = CreateCoordinator("owner-b", leaseDurationSeconds: 6, heartbeatIntervalSeconds: 2);
            var competingAttempt = await competingCoordinator.TryAcquireAsync(JobName, CancellationToken.None);
            Assert.Null(competingAttempt); // still held and kept alive by owner-a's heartbeat
        }

        // Disposing owner-a's lease released it - owner-b can now acquire immediately, with no expiry wait.
        var afterReleaseCoordinator = CreateCoordinator("owner-b", leaseDurationSeconds: 6, heartbeatIntervalSeconds: 2);
        await using var afterRelease = await afterReleaseCoordinator.TryAcquireAsync(JobName, CancellationToken.None);
        Assert.NotNull(afterRelease);
    }

    private JobLeaseCoordinator CreateCoordinator(string ownerId, int leaseDurationSeconds, int heartbeatIntervalSeconds)
    {
        var services = new ServiceCollection();
        services.AddDbContext<BookSpaceDbContext>(options => options.UseSqlServer(_connectionString));
        services.AddScoped<ICurrentUserContext>(_ => new FixedCurrentUserContext());
        services.AddScoped<IJobLeaseStore, JobLeaseStore>();
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var options = Options.Create(new BackgroundJobsOptions
        {
            LeaseDurationSeconds = leaseDurationSeconds,
            HeartbeatIntervalSeconds = heartbeatIntervalSeconds,
        });

        return new JobLeaseCoordinator(
            scopeFactory,
            options,
            new FixedInstanceIdentity(ownerId),
            TimeProvider.System,
            NullLogger<JobLeaseCoordinator>.Instance);
    }

    private BookSpaceDbContext CreateDbContext()
    {
        var dbOptions = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString).Options;
        return new BookSpaceDbContext(dbOptions, new FixedCurrentUserContext());
    }

    private sealed class FixedCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId => null;
        public Guid? TenantId => null;
        public IReadOnlyCollection<string> Roles => [];
    }

    private sealed class FixedInstanceIdentity(string ownerId) : IBackgroundJobInstanceIdentity
    {
        public string OwnerId => ownerId;
    }
}
