using BookSpace.Application.BackgroundJobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace BookSpace.Application.Tests.BackgroundJobs;

// Unit tests for JobLeaseCoordinator's own orchestration (heartbeat scheduling, lost-lease signalling,
// clean disposal) against a mocked IJobLeaseStore and a FakeTimeProvider - no database involved, no test
// here depends on a real heartbeat interval actually elapsing. The real acquire/renew/release SQL
// behavior against LocalDB is proven separately by JobLeaseStoreTests and
// JobLeaseCoordinatorIntegrationTests (BookSpace.Infrastructure.Tests).
public sealed class JobLeaseCoordinatorTests
{
    private const string JobName = "test-job";
    private const string OwnerId = "test-owner";
    private static readonly TimeSpan RealTimeTestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task TryAcquireAsync_WhenTheStoreAcquires_ReturnsALeaseCarryingThisInstancesIdentity()
    {
        var store = new Mock<IJobLeaseStore>();
        store.Setup(s => s.TryAcquireAsync(JobName, OwnerId, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTimeOffset.UtcNow.AddSeconds(60));
        var coordinator = CreateCoordinator(store.Object, out _);

        await using var lease = await coordinator.TryAcquireAsync(JobName, CancellationToken.None);

        Assert.NotNull(lease);
        Assert.Equal(JobName, lease!.JobName);
        Assert.Equal(OwnerId, lease.OwnerId);
    }

    [Fact]
    public async Task TryAcquireAsync_WhenTheStoreReportsAnotherOwnerHoldsIt_ReturnsNull()
    {
        var store = new Mock<IJobLeaseStore>();
        store.Setup(s => s.TryAcquireAsync(JobName, OwnerId, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTimeOffset?)null);
        var coordinator = CreateCoordinator(store.Object, out _);

        var lease = await coordinator.TryAcquireAsync(JobName, CancellationToken.None);

        Assert.Null(lease);
    }

    [Fact]
    public async Task TryAcquireAsync_WhenTheStoreThrowsAnUnexpectedError_ReturnsNullInsteadOfPropagating()
    {
        var store = new Mock<IJobLeaseStore>();
        store.Setup(s => s.TryAcquireAsync(JobName, OwnerId, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated database failure"));
        var coordinator = CreateCoordinator(store.Object, out _);

        var lease = await coordinator.TryAcquireAsync(JobName, CancellationToken.None);

        Assert.Null(lease);
    }

    [Fact]
    public async Task Heartbeat_RenewsWithTheSameJobNameAndOwnerAfterEachConfiguredInterval()
    {
        var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Mock<IJobLeaseStore>();
        store.Setup(s => s.TryAcquireAsync(JobName, OwnerId, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTimeOffset.UtcNow.AddSeconds(60));
        store.Setup(s => s.TryRenewAsync(JobName, OwnerId, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback(() => renewed.TrySetResult())
            .ReturnsAsync(DateTimeOffset.UtcNow.AddSeconds(60));
        var coordinator = CreateCoordinator(store.Object, out var timeProvider, leaseDurationSeconds: 60, heartbeatIntervalSeconds: 10);

        await using var lease = await coordinator.TryAcquireAsync(JobName, CancellationToken.None);
        Assert.NotNull(lease);

        timeProvider.Advance(TimeSpan.FromSeconds(10));
        await renewed.Task.WaitAsync(RealTimeTestTimeout);

        store.Verify(s => s.TryRenewAsync(JobName, OwnerId, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Heartbeat_WhenRenewFails_CancelsLeaseLostTokenAndStopsRenewingAfterThat()
    {
        var store = new Mock<IJobLeaseStore>();
        store.Setup(s => s.TryAcquireAsync(JobName, OwnerId, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTimeOffset.UtcNow.AddSeconds(60));
        store.Setup(s => s.TryRenewAsync(JobName, OwnerId, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTimeOffset?)null); // lost ownership - e.g. expired and taken over elsewhere
        var coordinator = CreateCoordinator(store.Object, out var timeProvider, leaseDurationSeconds: 60, heartbeatIntervalSeconds: 10);

        await using var lease = await coordinator.TryAcquireAsync(JobName, CancellationToken.None);
        Assert.NotNull(lease);

        var leaseLost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = lease!.LeaseLostToken.Register(() => leaseLost.TrySetResult());

        timeProvider.Advance(TimeSpan.FromSeconds(10));
        await leaseLost.Task.WaitAsync(RealTimeTestTimeout);

        Assert.True(lease.LeaseLostToken.IsCancellationRequested);
        store.Verify(s => s.TryRenewAsync(JobName, OwnerId, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);

        // The loop must not keep hammering the store once it knows it lost the lease.
        timeProvider.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        store.Verify(s => s.TryRenewAsync(JobName, OwnerId, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_StopsTheHeartbeatAndReleasesTheLease()
    {
        var store = new Mock<IJobLeaseStore>();
        store.Setup(s => s.TryAcquireAsync(JobName, OwnerId, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTimeOffset.UtcNow.AddSeconds(60));
        store.Setup(s => s.TryReleaseAsync(JobName, OwnerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var coordinator = CreateCoordinator(store.Object, out var timeProvider, leaseDurationSeconds: 60, heartbeatIntervalSeconds: 10);

        var lease = await coordinator.TryAcquireAsync(JobName, CancellationToken.None);
        Assert.NotNull(lease);

        await lease!.DisposeAsync();

        store.Verify(s => s.TryReleaseAsync(JobName, OwnerId, It.IsAny<CancellationToken>()), Times.Once);

        // Nothing left running to renew after disposal, even once more time passes.
        timeProvider.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        store.Verify(s => s.TryRenewAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DisposeAsync_WhenTheLeaseWasAlreadyLost_StillCompletesWithoutThrowing()
    {
        var store = new Mock<IJobLeaseStore>();
        store.Setup(s => s.TryAcquireAsync(JobName, OwnerId, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTimeOffset.UtcNow.AddSeconds(60));
        store.Setup(s => s.TryRenewAsync(JobName, OwnerId, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTimeOffset?)null);
        // Owner mismatch by the time dispose's release runs - another instance already took over.
        store.Setup(s => s.TryReleaseAsync(JobName, OwnerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var coordinator = CreateCoordinator(store.Object, out var timeProvider, leaseDurationSeconds: 60, heartbeatIntervalSeconds: 10);

        var lease = await coordinator.TryAcquireAsync(JobName, CancellationToken.None);
        Assert.NotNull(lease);

        var leaseLost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (lease!.LeaseLostToken.Register(() => leaseLost.TrySetResult()))
        {
            timeProvider.Advance(TimeSpan.FromSeconds(10));
            await leaseLost.Task.WaitAsync(RealTimeTestTimeout);
        }

        await lease.DisposeAsync(); // must not throw even though the lease was already lost
    }

    private static JobLeaseCoordinator CreateCoordinator(
        IJobLeaseStore store,
        out FakeTimeProvider timeProvider,
        int leaseDurationSeconds = 60,
        int heartbeatIntervalSeconds = 20)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        timeProvider = new FakeTimeProvider();
        var options = Options.Create(new BackgroundJobsOptions
        {
            LeaseDurationSeconds = leaseDurationSeconds,
            HeartbeatIntervalSeconds = heartbeatIntervalSeconds,
        });

        return new JobLeaseCoordinator(
            scopeFactory,
            options,
            new FixedInstanceIdentity(OwnerId),
            timeProvider,
            NullLogger<JobLeaseCoordinator>.Instance);
    }

    private sealed class FixedInstanceIdentity(string ownerId) : IBackgroundJobInstanceIdentity
    {
        public string OwnerId { get; } = ownerId;
    }
}
