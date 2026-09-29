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
        // Lease duration deliberately shorter than the total time this test holds it for - without a
        // working heartbeat renewing it, the lease would expire mid-test and the competing acquire below
        // would wrongly succeed too early.
        var coordinatorA = CreateCoordinator("owner-a", leaseDurationSeconds: 2, heartbeatIntervalSeconds: 1);

        await using (var lease = await coordinatorA.TryAcquireAsync(JobName, CancellationToken.None))
        {
            Assert.NotNull(lease);

            // Longer than one lease duration - only survives without the lease being stolen if the
            // heartbeat actually renewed it at least once in the meantime.
            await Task.Delay(TimeSpan.FromSeconds(5));

            Assert.False(lease!.LeaseLostToken.IsCancellationRequested);

            var competingCoordinator = CreateCoordinator("owner-b", leaseDurationSeconds: 2, heartbeatIntervalSeconds: 1);
            var competingAttempt = await competingCoordinator.TryAcquireAsync(JobName, CancellationToken.None);
            Assert.Null(competingAttempt); // still held and kept alive by owner-a's heartbeat
        }

        // Disposing owner-a's lease released it - owner-b can now acquire immediately, with no expiry wait.
        var afterReleaseCoordinator = CreateCoordinator("owner-b", leaseDurationSeconds: 2, heartbeatIntervalSeconds: 1);
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
