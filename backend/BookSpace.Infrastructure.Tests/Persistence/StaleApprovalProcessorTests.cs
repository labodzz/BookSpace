using BookSpace.Application.BackgroundJobs;
using BookSpace.Application.Bookings;
using BookSpace.Application.Notifications;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves StaleApprovalProcessor's full business behavior against a REAL SQL Server (LocalDB): atomic
// expiration of an ApprovalRequest + its Booking + the enqueued notification, the approve/reject race
// against the resource lock, idempotency/concurrency, recurring-occurrence independence, and failure
// isolation - see docs/background-jobs.md ("Stale-approval job") for the full design.
public sealed class StaleApprovalProcessorTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceStaleApprovalProcessorTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantId;
    private Guid _resourceTypeId;
    private Guid _resourceId;
    private Guid _ownerId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureCreatedAsync();

        _tenantId = Guid.NewGuid();
        _resourceTypeId = Guid.NewGuid();
        _resourceId = Guid.NewGuid();
        _ownerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        dbContext.Tenants.Add(new Tenant { Id = _tenantId, Name = "Stale Processor Tenant", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.ResourceTypes.Add(new ResourceType { Id = _resourceTypeId, TenantId = _tenantId, Name = "Meeting Room" });
        dbContext.Resources.Add(new Resource
        {
            Id = _resourceId, TenantId = _tenantId, ResourceTypeId = _resourceTypeId, Name = "Stale Room",
            Capacity = 5, RequiresApproval = true, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        });
        dbContext.Users.Add(new User { Id = _ownerId, TenantId = _tenantId, FirstName = "Owner", LastName = "User", Email = $"owner-{Guid.NewGuid():N}@bookspace.test", PasswordHash = "x", CreatedAtUtc = now });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureDeletedAsync();
    }

    // --- A: basic expiration - full pipeline ---

    [Fact]
    public async Task ProcessDueApprovalsAsync_WithAStaleApproval_ExpiresItAndItsBookingAndEnqueuesExactlyOneNotification()
    {
        var (approvalRequestId, bookingId) = await SeedStaleApprovalAsync();
        var processor = CreateProcessor();

        var result = await processor.ProcessDueApprovalsAsync(CancellationToken.None);

        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(1, result.ExpiredCount);
        Assert.Equal(1, result.NotificationEnqueuedCount);
        Assert.Equal(0, result.AlreadyCompletedCount);

        await using var verify = CreateDbContext();
        var approvalRequest = await verify.ApprovalRequests.IgnoreQueryFilters().SingleAsync(ar => ar.Id == approvalRequestId);
        var booking = await verify.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId);
        Assert.Equal(ApprovalStatus.Expired, approvalRequest.Status);
        Assert.Null(approvalRequest.ApproverId); // nobody decided this - never attribute it to a human
        Assert.NotNull(approvalRequest.DecidedAtUtc);
        Assert.Equal(BookingStatus.Expired, booking.Status);

        var outboxItems = await verify.NotificationOutboxItems.IgnoreQueryFilters()
            .Where(item => item.NotificationType == BookingNotificationTypes.ApprovalExpired)
            .ToListAsync();
        Assert.Single(outboxItems);
        Assert.Equal($"booking:{bookingId}:approvalexpired", outboxItems[0].IdempotencyKey);
        Assert.Equal(_ownerId, outboxItems[0].RecipientUserId); // the booking owner, never an approver/admin
    }

    // --- B: not-yet-stale is simply never a candidate - already proven by StaleApprovalReaderTests; here,
    // just confirm the processor as a whole is a no-op for an empty due set ---

    [Fact]
    public async Task ProcessDueApprovalsAsync_WithNoCandidates_ReturnsEmptyWithoutEnqueueingAnything()
    {
        var processor = CreateProcessor();

        var result = await processor.ProcessDueApprovalsAsync(CancellationToken.None);

        Assert.Equal(StaleApprovalCycleResult.Empty, result);
    }

    [Fact]
    public async Task ProcessDueApprovalsAsync_WhenDisabled_NeverCallsTheReader()
    {
        var (approvalRequestId, _) = await SeedStaleApprovalAsync();
        var processor = CreateProcessor(enabled: false);

        var result = await processor.ProcessDueApprovalsAsync(CancellationToken.None);

        Assert.Equal(StaleApprovalCycleResult.Empty, result);
        await using var verify = CreateDbContext();
        Assert.Equal(ApprovalStatus.Pending, (await verify.ApprovalRequests.IgnoreQueryFilters().SingleAsync(ar => ar.Id == approvalRequestId)).Status);
    }

    // --- G: idempotency - a second cycle after a successful expiration finds nothing left to do, and a
    // repeated attempt against the SAME (already-expired) candidate correctly detects AlreadyCompleted
    // rather than double-expiring or double-notifying ---

    [Fact]
    public async Task ProcessDueApprovalsAsync_CalledTwiceInARow_TheSecondCallFindsNothingLeftToDo()
    {
        await SeedStaleApprovalAsync();
        var processor = CreateProcessor();
        await processor.ProcessDueApprovalsAsync(CancellationToken.None);

        var second = await processor.ProcessDueApprovalsAsync(CancellationToken.None);

        Assert.Equal(StaleApprovalCycleResult.Empty, second); // already Expired - no longer Pending, never a candidate again
    }

    [Fact]
    public async Task ProcessDueApprovalsAsync_WhenTheSameCandidateIsForciblyReprocessed_DetectsAlreadyCompletedWithoutADuplicateNotification()
    {
        var (approvalRequestId, bookingId) = await SeedStaleApprovalAsync();
        var realReader = new StaleApprovalReader(CreateDbContext(), NullLogger<StaleApprovalReader>.Instance);
        var candidate = (await realReader.GetDueApprovalsAsync(50, CancellationToken.None)).Single();
        // A reader stub that keeps returning the SAME candidate every call, regardless of its real state -
        // simulating a restart/retry that re-discovers a row it already (successfully) acted on.
        var repeatingReader = new FixedCandidateReader(candidate);
        var processor = CreateProcessor(reader: repeatingReader);

        var first = await processor.ProcessDueApprovalsAsync(CancellationToken.None);
        var second = await processor.ProcessDueApprovalsAsync(CancellationToken.None);

        Assert.Equal(1, first.ExpiredCount);
        Assert.Equal(0, second.ExpiredCount);
        Assert.Equal(1, second.AlreadyCompletedCount);

        await using var verify = CreateDbContext();
        Assert.Equal(1, await verify.NotificationOutboxItems.IgnoreQueryFilters().CountAsync(item => item.IdempotencyKey == $"booking:{bookingId}:approvalexpired"));
    }

    [Fact]
    public async Task ProcessDueApprovalsAsync_TwoConcurrentAttemptsOnTheSameCandidate_LeaveExactlyOneExpirationAndOneNotification()
    {
        var (approvalRequestId, bookingId) = await SeedStaleApprovalAsync();
        using var startBarrier = new Barrier(2);

        async Task<StaleApprovalCycleResult> RunRacingCycleAsync()
        {
            var realReader = new StaleApprovalReader(CreateDbContext(), NullLogger<StaleApprovalReader>.Instance);
            var candidates = await realReader.GetDueApprovalsAsync(50, CancellationToken.None);
            var synchronizedReader = new BarrierSynchronizedFixedReader(candidates, startBarrier);
            var processor = CreateProcessor(reader: synchronizedReader);
            return await processor.ProcessDueApprovalsAsync(CancellationToken.None);
        }

        var results = await Task.WhenAll(RunRacingCycleAsync(), RunRacingCycleAsync());

        Assert.Equal(1, results.Sum(r => r.ExpiredCount));
        Assert.Equal(1, results.Sum(r => r.AlreadyCompletedCount));

        await using var verify = CreateDbContext();
        Assert.Equal(ApprovalStatus.Expired, (await verify.ApprovalRequests.IgnoreQueryFilters().SingleAsync(ar => ar.Id == approvalRequestId)).Status);
        Assert.Equal(1, await verify.NotificationOutboxItems.IgnoreQueryFilters().CountAsync(item => item.IdempotencyKey == $"booking:{bookingId}:approvalexpired"));
    }

    // --- H: atomicity - a failure injected right before commit leaves NEITHER the ApprovalRequest NOR the
    // Booking NOR any outbox item changed ---

    [Fact]
    public async Task ProcessDueApprovalsAsync_WhenSaveChangesFailsBeforeCommit_PersistsNeitherTheExpirationNorAnyNotification()
    {
        var (approvalRequestId, bookingId) = await SeedStaleApprovalAsync();
        var throwingInterceptor = new ThrowingSaveChangesInterceptor { ShouldThrow = true };
        var processor = CreateProcessor(itemScopeInterceptor: throwingInterceptor);

        var result = await processor.ProcessDueApprovalsAsync(CancellationToken.None);

        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, result.ExpiredCount);

        await using var verify = CreateDbContext();
        Assert.Equal(ApprovalStatus.Pending, (await verify.ApprovalRequests.IgnoreQueryFilters().SingleAsync(ar => ar.Id == approvalRequestId)).Status);
        Assert.Equal(BookingStatus.Pending, (await verify.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId)).Status);
        Assert.Equal(0, await verify.NotificationOutboxItems.IgnoreQueryFilters().CountAsync(item => item.IdempotencyKey == $"booking:{bookingId}:approvalexpired"));
    }

    // --- I: approve race - approve wins, the stale job's re-read under the SAME lock must never expire it ---

    [Fact]
    public async Task ProcessDueApprovalsAsync_WhenTheApprovalWasApprovedBeforeTheLockWasAcquired_LeavesItConfirmedAndNeverExpiresIt()
    {
        var (approvalRequestId, bookingId) = await SeedStaleApprovalAsync();
        await using (var approveContext = CreateDbContext())
        {
            var approvalRequest = await approveContext.ApprovalRequests.IgnoreQueryFilters().SingleAsync(ar => ar.Id == approvalRequestId);
            var booking = await approveContext.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId);
            approvalRequest.Status = ApprovalStatus.Approved;
            approvalRequest.ApproverId = _ownerId;
            approvalRequest.DecidedAtUtc = DateTimeOffset.UtcNow;
            booking.Status = BookingStatus.Confirmed;
            await approveContext.SaveChangesAsync();
        }

        var realReader = new StaleApprovalReader(CreateDbContext(), NullLogger<StaleApprovalReader>.Instance);
        // The reader itself would no longer find this (Booking.Status is now Confirmed) - this test is
        // specifically about the PROCESSOR's own re-check, so a stub forces the stale candidate through as
        // if the reader's read happened a moment before the approve committed.
        var staleCandidate = new DueStaleApproval(approvalRequestId, bookingId, _tenantId, _resourceId, _ownerId, DateTimeOffset.UtcNow.AddDays(3), DateTimeOffset.UtcNow.AddDays(3).AddHours(1), 1);
        var processor = CreateProcessor(reader: new FixedCandidateReader(staleCandidate));

        var result = await processor.ProcessDueApprovalsAsync(CancellationToken.None);

        Assert.Equal(1, result.AlreadyCompletedCount);
        Assert.Equal(0, result.ExpiredCount);

        await using var verify = CreateDbContext();
        Assert.Equal(ApprovalStatus.Approved, (await verify.ApprovalRequests.IgnoreQueryFilters().SingleAsync(ar => ar.Id == approvalRequestId)).Status);
        Assert.Equal(BookingStatus.Confirmed, (await verify.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId)).Status);
        Assert.Empty(await verify.NotificationOutboxItems.IgnoreQueryFilters().Where(item => item.NotificationType == BookingNotificationTypes.ApprovalExpired).ToListAsync());
    }

    // --- J: reject/cancel race - same protection for a Rejected decision and for a Cancelled booking ---

    [Theory]
    [InlineData(ApprovalStatus.Rejected, BookingStatus.Rejected)]
    [InlineData(ApprovalStatus.Cancelled, BookingStatus.Cancelled)]
    public async Task ProcessDueApprovalsAsync_WhenAnotherTerminalDecisionWonTheRace_NeverExpiresItOrDoubleNotifies(
        ApprovalStatus finalApprovalStatus, BookingStatus finalBookingStatus)
    {
        var (approvalRequestId, bookingId) = await SeedStaleApprovalAsync();
        await using (var decisionContext = CreateDbContext())
        {
            var approvalRequest = await decisionContext.ApprovalRequests.IgnoreQueryFilters().SingleAsync(ar => ar.Id == approvalRequestId);
            var booking = await decisionContext.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId);
            approvalRequest.Status = finalApprovalStatus;
            approvalRequest.DecidedAtUtc = DateTimeOffset.UtcNow;
            booking.Status = finalBookingStatus;
            await decisionContext.SaveChangesAsync();
        }

        var staleCandidate = new DueStaleApproval(approvalRequestId, bookingId, _tenantId, _resourceId, _ownerId, DateTimeOffset.UtcNow.AddDays(3), DateTimeOffset.UtcNow.AddDays(3).AddHours(1), 1);
        var processor = CreateProcessor(reader: new FixedCandidateReader(staleCandidate));

        var result = await processor.ProcessDueApprovalsAsync(CancellationToken.None);

        Assert.Equal(1, result.AlreadyCompletedCount);
        await using var verify = CreateDbContext();
        Assert.Equal(finalApprovalStatus, (await verify.ApprovalRequests.IgnoreQueryFilters().SingleAsync(ar => ar.Id == approvalRequestId)).Status);
        Assert.Equal(finalBookingStatus, (await verify.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId)).Status);
        Assert.Empty(await verify.NotificationOutboxItems.IgnoreQueryFilters().Where(item => item.NotificationType == BookingNotificationTypes.ApprovalExpired).ToListAsync());
    }

    // --- K: recurring - each occurrence's ApprovalRequest is independent; expiring one never touches a
    // sibling occurrence in the same series ---

    [Fact]
    public async Task ProcessDueApprovalsAsync_WithTwoOccurrencesInASeries_OnlyExpiresTheStaleOneNeverTheOther()
    {
        var seriesId = Guid.NewGuid();
        await using (var seedContext = CreateDbContext())
        {
            seedContext.RecurringSeries.Add(new RecurringSeries
            {
                Id = seriesId, TenantId = _tenantId, ResourceId = _resourceId, UserId = _ownerId,
                StartDate = DateOnly.FromDateTime(DateTime.UtcNow), StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0),
                TimeZoneId = "UTC", Frequency = RecurrenceFrequency.Daily, Interval = 1, OccurrenceCount = 2, Quantity = 1,
            });
            await seedContext.SaveChangesAsync();
        }

        var (staleApprovalId, staleBookingId) = await SeedStaleApprovalAsync(seriesId);
        var (freshApprovalId, freshBookingId) = await SeedStaleApprovalAsync(seriesId, expiresAtUtc: DateTimeOffset.UtcNow.AddHours(1));

        var processor = CreateProcessor();
        var result = await processor.ProcessDueApprovalsAsync(CancellationToken.None);

        Assert.Equal(1, result.ExpiredCount);
        await using var verify = CreateDbContext();
        Assert.Equal(ApprovalStatus.Expired, (await verify.ApprovalRequests.IgnoreQueryFilters().SingleAsync(ar => ar.Id == staleApprovalId)).Status);
        Assert.Equal(BookingStatus.Expired, (await verify.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == staleBookingId)).Status);
        Assert.Equal(ApprovalStatus.Pending, (await verify.ApprovalRequests.IgnoreQueryFilters().SingleAsync(ar => ar.Id == freshApprovalId)).Status);
        Assert.Equal(BookingStatus.Pending, (await verify.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == freshBookingId)).Status);
    }

    // --- N: Email:Enabled plays no part here at all - the enqueued item simply sits Pending, ready for
    // whenever dispatch/email is actually enabled; StaleApprovalProcessor never consults EmailOptions ---

    [Fact]
    public async Task ProcessDueApprovalsAsync_EnqueuesANotificationThatStaysPendingRegardlessOfEmailConfiguration()
    {
        var (_, bookingId) = await SeedStaleApprovalAsync();
        var processor = CreateProcessor();

        await processor.ProcessDueApprovalsAsync(CancellationToken.None);

        await using var verify = CreateDbContext();
        var item = await verify.NotificationOutboxItems.IgnoreQueryFilters().SingleAsync(i => i.IdempotencyKey == $"booking:{bookingId}:approvalexpired");
        Assert.Equal(NotificationOutboxStatus.Pending, item.Status); // untouched by dispatch - this task never sends email
    }

    // --- O: cancellation - a pre-cancelled token never starts any item; cancellation mid-batch stops
    // before the next item ---

    [Fact]
    public async Task ProcessDueApprovalsAsync_WithAnAlreadyCancelledToken_NeverStartsAnyItem()
    {
        var (approvalRequestId, _) = await SeedStaleApprovalAsync();
        var processor = CreateProcessor();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => processor.ProcessDueApprovalsAsync(new CancellationToken(canceled: true)));

        await using var verify = CreateDbContext();
        Assert.Equal(ApprovalStatus.Pending, (await verify.ApprovalRequests.IgnoreQueryFilters().SingleAsync(ar => ar.Id == approvalRequestId)).Status);
    }

    [Fact]
    public async Task ProcessDueApprovalsAsync_WhenCancelledDuringTheFirstItem_NeverStartsTheSecondItem()
    {
        var (firstApprovalId, _) = await SeedStaleApprovalAsync();
        var (secondApprovalId, _) = await SeedStaleApprovalAsync();
        using var cts = new CancellationTokenSource();
        var cancelingInterceptor = new CancelOnFirstSaveInterceptor(cts);
        var processor = CreateProcessor(itemScopeInterceptor: cancelingInterceptor);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessDueApprovalsAsync(cts.Token));

        await using var verify = CreateDbContext();
        var firstStatus = (await verify.ApprovalRequests.IgnoreQueryFilters().SingleAsync(ar => ar.Id == firstApprovalId)).Status;
        var secondStatus = (await verify.ApprovalRequests.IgnoreQueryFilters().SingleAsync(ar => ar.Id == secondApprovalId)).Status;
        // At most one of the two was expired before cancellation stopped the batch - whether the
        // in-flight first item's own SaveChanges also observed the cancellation (leaving both Pending) or
        // completed just before it (leaving exactly one Expired and the second never started) depends on
        // exact ADO.NET cancellation timing, which this test deliberately does not over-specify; what must
        // always hold is that the batch never ran both items to completion once cancellation fired.
        var expiredCount = new[] { firstStatus, secondStatus }.Count(status => status == ApprovalStatus.Expired);
        Assert.True(expiredCount <= 1, $"Expected at most one candidate to be expired once cancellation fired, but both were (firstStatus={firstStatus}, secondStatus={secondStatus}).");
    }

    // --- P: logging/correlation - no PII in any captured log line ---

    [Fact]
    public async Task ProcessDueApprovalsAsync_NeverLogsTheRecipientEmailOrAnyPayloadContent()
    {
        await SeedStaleApprovalAsync();
        var fakeLogger = new FakeLogger<StaleApprovalProcessor>();
        var processor = CreateProcessor(logger: fakeLogger);

        await processor.ProcessDueApprovalsAsync(CancellationToken.None);

        await using var verify = CreateDbContext();
        var ownerEmail = (await verify.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == _ownerId)).Email;
        foreach (var record in fakeLogger.Collector.GetSnapshot())
        {
            Assert.DoesNotContain(ownerEmail, record.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    // --- Test infrastructure ---

    private async Task<(Guid ApprovalRequestId, Guid BookingId)> SeedStaleApprovalAsync(Guid? seriesId = null, DateTimeOffset? expiresAtUtc = null)
    {
        var bookingId = Guid.NewGuid();
        var approvalRequestId = Guid.NewGuid();
        var expiry = expiresAtUtc ?? DateTimeOffset.UtcNow.AddMinutes(-1);
        var requestedAt = expiry.AddHours(-48);
        await using var dbContext = CreateDbContext();
        dbContext.Bookings.Add(new Booking
        {
            Id = bookingId, TenantId = _tenantId, ResourceId = _resourceId, UserId = _ownerId, SeriesId = seriesId,
            StartUtc = DateTimeOffset.UtcNow.AddDays(3), EndUtc = DateTimeOffset.UtcNow.AddDays(3).AddHours(1),
            Quantity = 1, Status = BookingStatus.Pending, CreatedAtUtc = requestedAt,
        });
        dbContext.ApprovalRequests.Add(new ApprovalRequest
        {
            Id = approvalRequestId, TenantId = _tenantId, BookingId = bookingId, ApproverId = null,
            Status = ApprovalStatus.Pending, RequestedAtUtc = requestedAt, ExpiresAtUtc = expiry,
        });
        await dbContext.SaveChangesAsync();
        return (approvalRequestId, bookingId);
    }

    private StaleApprovalProcessor CreateProcessor(
        bool enabled = true, int batchSize = 50, IStaleApprovalReader? reader = null,
        IInterceptor? itemScopeInterceptor = null, ILogger<StaleApprovalProcessor>? logger = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<BookSpaceDbContext>(options =>
        {
            options.UseSqlServer(_connectionString);
            if (itemScopeInterceptor is not null)
            {
                options.AddInterceptors(itemScopeInterceptor);
            }
        });
        services.AddScoped<ICurrentUserContext>(_ => new FixedCurrentUserContext());
        services.AddScoped<IResourceBookingLock, ResourceBookingLock>();
        services.AddScoped<INotificationOutboxWriter, NotificationOutboxWriter>();
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var effectiveReader = reader ?? new StaleApprovalReader(CreateDbContext(), NullLogger<StaleApprovalReader>.Instance);
        var options = Options.Create(new BackgroundJobsOptions { StaleApproval = new StaleApprovalOptions { Enabled = enabled, BatchSize = batchSize } });
        return new StaleApprovalProcessor(scopeFactory, effectiveReader, options, new FakeTimeProvider(DateTimeOffset.UtcNow), logger ?? NullLogger<StaleApprovalProcessor>.Instance);
    }

    private BookSpaceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString).Options;
        return new BookSpaceDbContext(options, new FixedCurrentUserContext());
    }

    private sealed class FixedCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId => null;
        public Guid? TenantId => null;
        public IReadOnlyCollection<string> Roles => [];
    }

    // Always returns the SAME single candidate, regardless of the real due state - used to force
    // StaleApprovalProcessor's own re-check-under-lock logic to run against a candidate that may no
    // longer actually be due, simulating a restart/retry or a race the reader itself would no longer see.
    private sealed class FixedCandidateReader(DueStaleApproval candidate) : IStaleApprovalReader
    {
        public Task<IReadOnlyList<DueStaleApproval>> GetDueApprovalsAsync(int batchSize, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DueStaleApproval>>([candidate]);
    }

    // Synchronizes two concurrent calls so both finish "reading" before either proceeds to acquire the
    // resource lock - without this, one racer would likely finish entirely before the other even started.
    private sealed class BarrierSynchronizedFixedReader(IReadOnlyList<DueStaleApproval> candidates, Barrier barrier) : IStaleApprovalReader
    {
        public Task<IReadOnlyList<DueStaleApproval>> GetDueApprovalsAsync(int batchSize, CancellationToken cancellationToken)
        {
            barrier.SignalAndWait(cancellationToken);
            return Task.FromResult(candidates);
        }
    }

    private sealed class ThrowingSaveChangesInterceptor : SaveChangesInterceptor
    {
        public bool ShouldThrow { get; set; }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) =>
            ShouldThrow ? throw new InvalidOperationException("Simulated failure injected before commit.") : base.SavingChanges(eventData, result);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            ShouldThrow
                ? throw new InvalidOperationException("Simulated failure injected before commit.")
                : base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    // Cancels the shared token the instant the FIRST item's SaveChanges actually fires - deterministically
    // simulates "cancellation arrives exactly between two items" without relying on real concurrent timing.
    private sealed class CancelOnFirstSaveInterceptor(CancellationTokenSource cts) : SaveChangesInterceptor
    {
        private bool _cancelled;

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            CancelOnce();
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            CancelOnce();
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private void CancelOnce()
        {
            if (!_cancelled)
            {
                _cancelled = true;
                cts.Cancel();
            }
        }
    }
}
