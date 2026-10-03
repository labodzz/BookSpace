using BookSpace.Api.BackgroundJobs;
using BookSpace.Application.BackgroundJobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BookSpace.Api.Tests.BackgroundJobs;

// Unit tests against BackgroundJobsWorker directly (no WebApplicationFactory/real host needed) - a
// minimal ServiceCollection supplies IServiceScopeFactory, and FakeTimeProvider stands in for real
// time so no test here depends on an actual 30-second (or any) real delay elapsing. Every test that
// isn't specifically about lease behavior uses AlwaysAcquiringLeaseCoordinator, a trivial stub that
// always "wins" the lease instantly - the real acquire/renew/release SQL is exercised separately (real
// LocalDB) in BookSpace.Infrastructure.Tests, and JobLeaseCoordinator's own heartbeat scheduling is
// exercised separately in BookSpace.Application.Tests.
public sealed class BackgroundJobsWorkerTests
{
    private static readonly TimeSpan RealTimeTestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task StartAsync_ThenStopAsync_CompletesWithoutThrowing()
    {
        var recorder = new CycleRecorder();
        var scopeFactory = BuildScopeFactory(recorder);
        var worker = CreateWorker(scopeFactory, new FakeTimeProvider());

        await worker.StartAsync(CancellationToken.None);
        await recorder.WaitForCountAsync(1, RealTimeTestTimeout);
        await worker.StopAsync(CancellationToken.None);
    }

    // pollIntervalSeconds is deliberately huge (1 hour) - with a FakeTimeProvider that never advances
    // on its own, the only way Task.Delay(pollInterval, timeProvider, stoppingToken) can ever complete
    // is via the token being cancelled by StopAsync. The bounded Task.WhenAny below turns "StopAsync
    // hung waiting out the interval" into a clear test failure instead of an actual hang.
    [Fact]
    public async Task StopAsync_WhileWaitingForTheNextPoll_ReturnsPromptlyRatherThanWaitingOutTheFullInterval()
    {
        var recorder = new CycleRecorder();
        var scopeFactory = BuildScopeFactory(recorder);
        var worker = CreateWorker(scopeFactory, new FakeTimeProvider(), pollIntervalSeconds: 3600);

        await worker.StartAsync(CancellationToken.None);
        await recorder.WaitForCountAsync(1, RealTimeTestTimeout);

        var stopTask = worker.StopAsync(CancellationToken.None);
        var winner = await Task.WhenAny(stopTask, Task.Delay(RealTimeTestTimeout));

        Assert.Same(stopTask, winner);
        await stopTask;
    }

    // Advancing the FakeTimeProvider (rather than waiting on a real clock) is what lets this test
    // deterministically force a second cycle without depending on any real elapsed time.
    [Fact]
    public async Task SeparateCycles_EachResolveIBackgroundJobCycleFromASeparateScope()
    {
        var recorder = new CycleRecorder();
        var scopeFactory = BuildScopeFactory(recorder);
        var timeProvider = new FakeTimeProvider();
        var worker = CreateWorker(scopeFactory, timeProvider, pollIntervalSeconds: 30);

        await worker.StartAsync(CancellationToken.None);
        await recorder.WaitForCountAsync(1, RealTimeTestTimeout);

        timeProvider.Advance(TimeSpan.FromSeconds(30));
        await recorder.WaitForCountAsync(2, RealTimeTestTimeout);

        await worker.StopAsync(CancellationToken.None);

        var instanceIds = recorder.InstanceIds;
        Assert.True(instanceIds.Count >= 2);
        Assert.NotEqual(instanceIds[0], instanceIds[1]);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDisabledByConfiguration_NeverRunsACycle()
    {
        var recorder = new CycleRecorder();
        var scopeFactory = BuildScopeFactory(recorder);
        var worker = CreateWorker(scopeFactory, new FakeTimeProvider(), enabled: false);

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.Empty(recorder.InstanceIds);
    }

    // The lease-integration half of docs/background-jobs.md's "Job lease lock" protocol: when the
    // coordinator reports the lease is already held elsewhere, the worker must skip the cycle entirely
    // rather than run IBackgroundJobCycle anyway.
    [Fact]
    public async Task RunCycleAsync_WhenTheLeaseIsNotAcquired_NeverRunsTheCycle()
    {
        var recorder = new CycleRecorder();
        var scopeFactory = BuildScopeFactory(recorder);
        var timeProvider = new FakeTimeProvider();
        var worker = CreateWorker(scopeFactory, timeProvider, leaseCoordinator: new StubJobLeaseCoordinator(acquires: false), pollIntervalSeconds: 30);

        await worker.StartAsync(CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        await worker.StopAsync(CancellationToken.None);

        Assert.Empty(recorder.InstanceIds);
    }

    // A heartbeat losing the lease mid-cycle (JobLeaseCoordinator cancels IJobLease.LeaseLostToken) must
    // reach the running cycle as a cancellation signal - proven here by a cycle that blocks until its
    // token is cancelled, with the worker's own linked-token wiring being the only thing that could ever
    // unblock it.
    [Fact]
    public async Task RunCycleAsync_WhenTheLeaseIsLostMidCycle_CancelsTheCyclesToken()
    {
        var services = new ServiceCollection();
        var blockingCycle = new BlockingUntilCancelledCycle();
        services.AddSingleton<IBackgroundJobCycle>(blockingCycle);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var stubLease = new StubJobLease();
        var worker = CreateWorker(scopeFactory, new FakeTimeProvider(), leaseCoordinator: new StubJobLeaseCoordinator(lease: stubLease));

        await worker.StartAsync(CancellationToken.None);
        await blockingCycle.WaitUntilStartedAsync(RealTimeTestTimeout);

        stubLease.SimulateLeaseLost();

        await blockingCycle.WaitUntilCancelledAsync(RealTimeTestTimeout);
        await worker.StopAsync(CancellationToken.None);
    }

    // The missing direct proof (WP-8 audit) that BackgroundJobsWorker.RunCycleAsync's broad catch around
    // IBackgroundJobCycle.RunCycleAsync does what its own comment claims: a single cycle's unexpected
    // exception is logged and isolated, never silently killing the hosted service for the rest of the
    // app's lifetime - the worker goes on to attempt a brand new cycle on the very next poll interval.
    // FakeTimeProvider.Advance (not a real elapsed delay) is what makes "the next poll interval" happen
    // deterministically and instantly in a test.
    [Fact]
    public async Task RunCycleAsync_WhenACycleThrowsUnexpectedly_LogsItAndStillAttemptsANewCycleAfterTheNextPollInterval()
    {
        var recorder = new CycleRecorder();
        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        // A fresh scope (and therefore a fresh ThrowsOnceThenRecordsCycle instance) backs every cycle -
        // same as the real worker - so "has it already thrown" must live in a singleton shared across
        // instances, not on the scoped cycle instance itself.
        services.AddSingleton<ThrowOnceGate>();
        services.AddScoped<IBackgroundJobCycle, ThrowsOnceThenRecordsCycle>();
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var timeProvider = new FakeTimeProvider();
        var fakeLogger = new FakeLogger<BackgroundJobsWorker>();
        var worker = new BackgroundJobsWorker(
            scopeFactory,
            new StubJobLeaseCoordinator(),
            Options.Create(new BackgroundJobsOptions { Enabled = true, PollIntervalSeconds = 30 }),
            timeProvider,
            fakeLogger);

        await worker.StartAsync(CancellationToken.None);

        // The first cycle throws - give the loop a moment to reach and catch it before advancing time,
        // by waiting for the error to actually be logged rather than an arbitrary delay.
        await WaitForLogAsync(fakeLogger, LogLevel.Error, RealTimeTestTimeout);

        timeProvider.Advance(TimeSpan.FromSeconds(30));
        await recorder.WaitForCountAsync(1, RealTimeTestTimeout);

        await worker.StopAsync(CancellationToken.None);

        Assert.Contains(
            fakeLogger.Collector.GetSnapshot(),
            record => record.Level == LogLevel.Error && record.Exception is InvalidOperationException);
    }

    private static async Task WaitForLogAsync(FakeLogger logger, LogLevel level, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!logger.Collector.GetSnapshot().Any(record => record.Level == level))
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token);
        }
    }

    private static IServiceScopeFactory BuildScopeFactory(CycleRecorder recorder)
    {
        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        services.AddScoped<IBackgroundJobCycle, RecordingCycle>();
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static BackgroundJobsWorker CreateWorker(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        bool enabled = true,
        int pollIntervalSeconds = 30,
        IJobLeaseCoordinator? leaseCoordinator = null) =>
        new(
            scopeFactory,
            leaseCoordinator ?? new StubJobLeaseCoordinator(),
            Options.Create(new BackgroundJobsOptions { Enabled = enabled, PollIntervalSeconds = pollIntervalSeconds }),
            timeProvider,
            NullLogger<BackgroundJobsWorker>.Instance);

    // Always "wins" the lease instantly (unless told not to) - the real acquire/renew/release SQL is
    // exercised separately against LocalDB, not here.
    private sealed class StubJobLeaseCoordinator(bool acquires = true, IJobLease? lease = null) : IJobLeaseCoordinator
    {
        public Task<IJobLease?> TryAcquireAsync(string jobName, CancellationToken cancellationToken) =>
            Task.FromResult(acquires ? lease ?? new StubJobLease() : null);
    }

    private sealed class StubJobLease : IJobLease
    {
        private readonly CancellationTokenSource _leaseLostCts = new();

        public string JobName => BackgroundJobsWorker.PrimaryCycleJobName;
        public string OwnerId => "stub-owner";
        public CancellationToken LeaseLostToken => _leaseLostCts.Token;

        public void SimulateLeaseLost() => _leaseLostCts.Cancel();

        public ValueTask DisposeAsync()
        {
            _leaseLostCts.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    // Blocks on its own cancellation token until cancelled, then records that it observed cancellation -
    // the test spy for proving the worker's linked token (stoppingToken + lease.LeaseLostToken) actually
    // reaches IBackgroundJobCycle.RunCycleAsync.
    private sealed class BlockingUntilCancelledCycle : IBackgroundJobCycle
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _observedCancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RunCycleAsync(CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _observedCancellation.TrySetResult();
                throw;
            }
        }

        public Task WaitUntilStartedAsync(TimeSpan timeout) => _started.Task.WaitAsync(timeout);

        public Task WaitUntilCancelledAsync(TimeSpan timeout) => _observedCancellation.Task.WaitAsync(timeout);
    }

    // Shared (singleton) across every per-cycle scope, so "has the one simulated failure already
    // happened" survives across the fresh ThrowsOnceThenRecordsCycle instance each new scope creates.
    private sealed class ThrowOnceGate
    {
        private int _hasThrown;

        public bool ShouldThrow() => Interlocked.Exchange(ref _hasThrown, 1) == 0;
    }

    // Throws on the very first cycle attempt across the whole test (simulating an unexpected bug in a
    // real IBackgroundJobCycle), then behaves exactly like RecordingCycle on every later one - the test
    // spy for RunCycleAsync_WhenACycleThrowsUnexpectedly_LogsItAndStillAttemptsANewCycleAfterTheNextPollInterval.
    private sealed class ThrowsOnceThenRecordsCycle(CycleRecorder recorder, ThrowOnceGate gate) : IBackgroundJobCycle
    {
        public Task RunCycleAsync(CancellationToken cancellationToken)
        {
            if (gate.ShouldThrow())
            {
                throw new InvalidOperationException("Simulated unexpected failure on the first cycle attempt.");
            }

            recorder.Record(Guid.NewGuid());
            return Task.CompletedTask;
        }
    }

    // Records which *instance* ran each cycle (not just how many) - the test spy for proving a fresh
    // IServiceScope (and therefore a fresh scoped IBackgroundJobCycle) backs every cycle.
    private sealed class RecordingCycle(CycleRecorder recorder) : IBackgroundJobCycle
    {
        private readonly Guid _instanceId = Guid.NewGuid();

        public Task RunCycleAsync(CancellationToken cancellationToken)
        {
            recorder.Record(_instanceId);
            return Task.CompletedTask;
        }
    }

    // Thread-safe recorder + a wait primitive so tests can deterministically await "N cycles have run"
    // instead of an arbitrary Thread.Sleep.
    private sealed class CycleRecorder
    {
        private readonly List<Guid> _instanceIds = [];
        private readonly Lock _lock = new();
        private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<Guid> InstanceIds
        {
            get { lock (_lock) { return _instanceIds.ToArray(); } }
        }

        public void Record(Guid instanceId)
        {
            TaskCompletionSource previousSignal;
            lock (_lock)
            {
                _instanceIds.Add(instanceId);
                previousSignal = _signal;
                _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            previousSignal.TrySetResult();
        }

        public async Task WaitForCountAsync(int count, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (true)
            {
                Task currentSignal;
                lock (_lock)
                {
                    if (_instanceIds.Count >= count)
                    {
                        return;
                    }
                    currentSignal = _signal.Task;
                }
                await currentSignal.WaitAsync(cts.Token);
            }
        }
    }
}
