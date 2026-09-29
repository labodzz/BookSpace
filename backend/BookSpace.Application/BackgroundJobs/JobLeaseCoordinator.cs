using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookSpace.Application.BackgroundJobs;

// Registered as a singleton (see DependencyInjection.AddApplication) - it never holds a scoped
// IJobLeaseStore itself, only IServiceScopeFactory, and creates a fresh IServiceScope for every single
// acquire/renew/release call, exactly the same pattern BackgroundJobsWorker uses for IBackgroundJobCycle.
// See docs/background-jobs.md ("Job lease lock") for the full protocol this implements.
public sealed class JobLeaseCoordinator(
    IServiceScopeFactory scopeFactory,
    IOptions<BackgroundJobsOptions> options,
    IBackgroundJobInstanceIdentity instanceIdentity,
    TimeProvider timeProvider,
    ILogger<JobLeaseCoordinator> logger) : IJobLeaseCoordinator
{
    public async Task<IJobLease?> TryAcquireAsync(string jobName, CancellationToken cancellationToken)
    {
        var ownerId = instanceIdentity.OwnerId;
        var leaseDuration = TimeSpan.FromSeconds(options.Value.LeaseDurationSeconds);
        var heartbeatInterval = TimeSpan.FromSeconds(options.Value.HeartbeatIntervalSeconds);

        var expiresAtUtc = await RunStoreOperationAsync(
            jobName,
            ownerId,
            "acquire",
            store => store.TryAcquireAsync(jobName, ownerId, leaseDuration, cancellationToken),
            cancellationToken);

        if (expiresAtUtc is null)
        {
            logger.LogInformation(
                "Job lease acquire skipped: {JobName} is already owned by another instance; {OwnerId} did not win this cycle.",
                jobName,
                ownerId);
            return null;
        }

        logger.LogInformation(
            "Job lease acquire succeeded: {JobName} owned by {OwnerId}, expires at {LeaseExpiresAtUtc}.",
            jobName,
            ownerId,
            expiresAtUtc);

        return new JobLeaseHandle(this, jobName, ownerId, leaseDuration, heartbeatInterval, timeProvider, logger);
    }

    // Called only by JobLeaseHandle's own heartbeat loop.
    private Task<DateTimeOffset?> RenewAsync(string jobName, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken) =>
        RunStoreOperationAsync(
            jobName,
            ownerId,
            "renew",
            store => store.TryRenewAsync(jobName, ownerId, leaseDuration, cancellationToken),
            cancellationToken);

    // Called only by JobLeaseHandle.DisposeAsync. Uses CancellationToken.None deliberately: a release is
    // best-effort cleanup running during shutdown/cycle-end and must not itself be cut short by the very
    // token that is being cancelled.
    private async Task<bool> ReleaseAsync(string jobName, string ownerId)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IJobLeaseStore>();
            var released = await store.TryReleaseAsync(jobName, ownerId, CancellationToken.None);

            if (released)
            {
                logger.LogInformation("Job lease release succeeded: {JobName} released by {OwnerId}.", jobName, ownerId);
            }
            else
            {
                logger.LogInformation(
                    "Job lease release ignored: {JobName} is no longer owned by {OwnerId} (already lost/expired) - leaving it alone.",
                    jobName,
                    ownerId);
            }

            return released;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected database error releasing job lease {JobName} for {OwnerId}.", jobName, ownerId);
            return false;
        }
    }

    private async Task<DateTimeOffset?> RunStoreOperationAsync(
        string jobName,
        string ownerId,
        string operationName,
        Func<IJobLeaseStore, Task<DateTimeOffset?>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IJobLeaseStore>();
            return await operation(store);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Unexpected database error during job lease {Operation} for {JobName} owned by {OwnerId}.", operationName, jobName, ownerId);
            return null;
        }
    }

    // One held lease. Owns its heartbeat loop for its entire lifetime: started the moment the lease is
    // acquired, stopped only by DisposeAsync or by the loop itself discovering the lease was lost.
    private sealed class JobLeaseHandle : IJobLease
    {
        private readonly JobLeaseCoordinator _coordinator;
        private readonly TimeSpan _leaseDuration;
        private readonly TimeSpan _heartbeatInterval;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _leaseLostCts = new();
        private readonly CancellationTokenSource _stoppingCts = new();
        private readonly Task _heartbeatTask;
        private int _disposed;

        public JobLeaseHandle(
            JobLeaseCoordinator coordinator,
            string jobName,
            string ownerId,
            TimeSpan leaseDuration,
            TimeSpan heartbeatInterval,
            TimeProvider timeProvider,
            ILogger logger)
        {
            _coordinator = coordinator;
            JobName = jobName;
            OwnerId = ownerId;
            _leaseDuration = leaseDuration;
            _heartbeatInterval = heartbeatInterval;
            _timeProvider = timeProvider;
            _logger = logger;
            _heartbeatTask = RunHeartbeatLoopAsync();
        }

        public string JobName { get; }
        public string OwnerId { get; }
        public CancellationToken LeaseLostToken => _leaseLostCts.Token;

        private async Task RunHeartbeatLoopAsync()
        {
            try
            {
                while (!_stoppingCts.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(_heartbeatInterval, _timeProvider, _stoppingCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (_stoppingCts.IsCancellationRequested)
                    {
                        break;
                    }

                    var renewedExpiry = await _coordinator.RenewAsync(JobName, OwnerId, _leaseDuration, _stoppingCts.Token);
                    if (renewedExpiry is null)
                    {
                        _logger.LogWarning(
                            "Job lease heartbeat lost: {JobName} is no longer owned by {OwnerId}; signalling cancellation to whatever is running under this lease.",
                            JobName,
                            OwnerId);
                        await _leaseLostCts.CancelAsync();
                        break;
                    }

                    _logger.LogDebug(
                        "Job lease heartbeat renewed: {JobName} owned by {OwnerId}, expires at {LeaseExpiresAtUtc}.",
                        JobName,
                        OwnerId,
                        renewedExpiry);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The heartbeat loop must never bring the process down - an unexpected bug here is a
                // background-job infrastructure concern, not a reason to crash whatever is running under
                // the lease.
                _logger.LogError(ex, "Unexpected error in job lease heartbeat loop for {JobName} owned by {OwnerId}.", JobName, OwnerId);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _stoppingCts.CancelAsync();
            try
            {
                await _heartbeatTask;
            }
            catch
            {
                // The heartbeat loop already logs its own failures above; disposal itself must never throw.
            }

            await _coordinator.ReleaseAsync(JobName, OwnerId);

            _leaseLostCts.Dispose();
            _stoppingCts.Dispose();
        }
    }
}
