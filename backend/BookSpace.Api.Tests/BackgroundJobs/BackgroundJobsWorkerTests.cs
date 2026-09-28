using BookSpace.Api.BackgroundJobs;
using BookSpace.Application.BackgroundJobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BookSpace.Api.Tests.BackgroundJobs;

// Unit tests against BackgroundJobsWorker directly (no WebApplicationFactory/real host needed) - a
// minimal ServiceCollection supplies IServiceScopeFactory, and FakeTimeProvider stands in for real
// time so no test here depends on an actual 30-second (or any) real delay elapsing.
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

    private static IServiceScopeFactory BuildScopeFactory(CycleRecorder recorder)
    {
        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        services.AddScoped<IBackgroundJobCycle, RecordingCycle>();
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static BackgroundJobsWorker CreateWorker(
        IServiceScopeFactory scopeFactory, TimeProvider timeProvider, bool enabled = true, int pollIntervalSeconds = 30) =>
        new(
            scopeFactory,
            Options.Create(new BackgroundJobsOptions { Enabled = enabled, PollIntervalSeconds = pollIntervalSeconds }),
            timeProvider,
            NullLogger<BackgroundJobsWorker>.Instance);

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
