using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using BookSpace.Application.Notifications;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves, against real SQL Server (LocalDB), that a cascade handler's booking mutations and ALL of its
// notification outbox rows commit atomically - either every mutated occurrence AND every one of its
// outbox rows land, or none of it does. See docs/background-jobs.md ("Booking lifecycle notifications -
// recurring series") for the failure mode this closes: looping over EnqueueAsync (which self-commits via
// SaveChangesAsync) could only ever make the FIRST call's commit cover the staged business mutations -
// a crash between two cascaded calls left already-saved occurrences without their notification.
// EnqueueManyAsync replaces that loop with exactly one SaveChangesAsync covering everything.
public sealed class NotificationOutboxCascadeAtomicityTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceCascadeAtomicityTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantId;
    private Guid _resourceTypeId;
    private Guid _ownerId;
    private Guid _approverId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext(Guid.NewGuid());
        await dbContext.Database.EnsureCreatedAsync();

        _tenantId = Guid.NewGuid();
        _resourceTypeId = Guid.NewGuid();
        _ownerId = Guid.NewGuid();
        _approverId = Guid.NewGuid();

        var now = DateTimeOffset.UtcNow;
        dbContext.Tenants.Add(new Tenant { Id = _tenantId, Name = "Cascade Atomicity Tenant", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.ResourceTypes.Add(new ResourceType { Id = _resourceTypeId, TenantId = _tenantId, Name = "Meeting Room" });
        dbContext.Users.AddRange(
            new User { Id = _ownerId, TenantId = _tenantId, FirstName = "Owner", LastName = "User", Email = "owner@cascade-atomicity.test", PasswordHash = "x", CreatedAtUtc = now },
            new User { Id = _approverId, TenantId = _tenantId, FirstName = "Approver", LastName = "User", Email = "approver@cascade-atomicity.test", PasswordHash = "x", CreatedAtUtc = now });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext(Guid.NewGuid());
        await dbContext.Database.EnsureDeletedAsync();
    }

    // Test category 1 (recurring create): every occurrence this call actually confirms, AND every one of
    // their confirmation outbox rows, committed together.
    [Fact]
    public async Task CreateRecurringSeriesCommandHandler_WithSeveralConfirmedOccurrences_CommitsEveryOccurrenceAndEveryConfirmationTogether()
    {
        var resourceId = await SeedResourceAsync(capacity: 8, requiresApproval: false);
        var startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));

        await using var dbContext = CreateDbContext(_tenantId, _ownerId);
        var handler = CreateSeriesHandler(dbContext);

        var result = await handler.Handle(
            new CreateRecurringSeriesCommandRequest(resourceId, startDate, new TimeOnly(9, 0), new TimeOnly(10, 0),
                RecurrenceFrequency.Daily, Interval: 1, EndDate: null, OccurrenceCount: 4, Quantity: 1),
            CancellationToken.None);

        Assert.Equal(4, result.CreatedOccurrences.Count);
        Assert.All(result.CreatedOccurrences, occurrence => Assert.Equal(BookingStatus.Confirmed, occurrence.Status));

        await using var verify = CreateDbContext(_tenantId);
        foreach (var occurrence in result.CreatedOccurrences)
        {
            var booking = await verify.Bookings.SingleAsync(b => b.Id == occurrence.Id);
            Assert.Equal(BookingStatus.Confirmed, booking.Status);
            var outboxCount = await verify.NotificationOutboxItems.IgnoreQueryFilters()
                .CountAsync(i => i.IdempotencyKey == $"booking:{occurrence.Id}:confirmation");
            Assert.Equal(1, outboxCount);
        }
    }

    // Test category 2 (approve cascade): the primary booking AND every cascaded sibling ApproveRemainingSeries
    // actually approves, AND every one of their confirmation outbox rows, committed together.
    [Fact]
    public async Task ApproveBookingCommandHandler_WithApproveRemainingSeries_CommitsEveryApprovedOccurrenceAndEveryConfirmationTogether()
    {
        var resourceId = await SeedResourceAsync(capacity: 8, requiresApproval: true);
        var seriesId = await SeedSeriesAsync(resourceId);
        var anchor = DateTimeOffset.UtcNow.AddDays(3);
        var primary = await SeedPendingOccurrenceAsync(resourceId, seriesId, anchor);
        var sibling1 = await SeedPendingOccurrenceAsync(resourceId, seriesId, anchor.AddDays(1));
        var sibling2 = await SeedPendingOccurrenceAsync(resourceId, seriesId, anchor.AddDays(2));

        await using var dbContext = CreateDbContext(_tenantId, _approverId);
        var handler = CreateApproveHandler(dbContext);

        var result = await handler.Handle(new ApproveBookingCommandRequest(primary, "Approved for the series", ApproveRemainingSeries: true), CancellationToken.None);

        // GetBySeriesIdAsync makes no ordering guarantee - this test is about atomicity, not sibling order.
        Assert.Equal(new HashSet<Guid> { sibling1, sibling2 }, result.CascadedApprovedOccurrenceIds.ToHashSet());

        await using var verify = CreateDbContext(_tenantId);
        foreach (var bookingId in new[] { primary, sibling1, sibling2 })
        {
            var booking = await verify.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.Confirmed, booking.Status);
            var outboxCount = await verify.NotificationOutboxItems.IgnoreQueryFilters()
                .CountAsync(i => i.IdempotencyKey == $"booking:{bookingId}:confirmation");
            Assert.Equal(1, outboxCount);
        }
    }

    // Test category 3 (cancel cascade): the primary booking AND every cascaded sibling CancelRemainingSeries
    // actually cancels, AND every one of their cancellation outbox rows, committed together.
    [Fact]
    public async Task CancelBookingCommandHandler_WithCancelRemainingSeries_CommitsEveryCancelledOccurrenceAndEveryCancellationTogether()
    {
        var resourceId = await SeedResourceAsync(capacity: 8, requiresApproval: false);
        var seriesId = await SeedSeriesAsync(resourceId);
        var anchor = DateTimeOffset.UtcNow.AddDays(3);
        var primary = await SeedConfirmedOccurrenceAsync(resourceId, seriesId, anchor);
        var sibling1 = await SeedConfirmedOccurrenceAsync(resourceId, seriesId, anchor.AddDays(1));
        var sibling2 = await SeedConfirmedOccurrenceAsync(resourceId, seriesId, anchor.AddDays(2));

        await using var dbContext = CreateDbContext(_tenantId, _ownerId);
        var handler = CreateCancelHandler(dbContext);

        var result = await handler.Handle(new CancelBookingCommandRequest(primary, CancelRemainingSeries: true), CancellationToken.None);

        // GetBySeriesIdAsync makes no ordering guarantee - this test is about atomicity, not sibling order.
        Assert.Equal(new HashSet<Guid> { sibling1, sibling2 }, result.CascadedOccurrenceIds.ToHashSet());

        await using var verify = CreateDbContext(_tenantId);
        foreach (var bookingId in new[] { primary, sibling1, sibling2 })
        {
            var booking = await verify.Bookings.SingleAsync(b => b.Id == bookingId);
            Assert.Equal(BookingStatus.Cancelled, booking.Status);
            var outboxCount = await verify.NotificationOutboxItems.IgnoreQueryFilters()
                .CountAsync(i => i.IdempotencyKey == $"booking:{bookingId}:cancellation");
            Assert.Equal(1, outboxCount);
        }
    }

    // Test category 4 (simulated failure, no IResourceBookingLock wrapping this handler - see
    // CancelBookingCommandRequest's own class comment): a failure injected at the exact point
    // SaveChangesAsync would run must leave NEITHER the cascaded booking mutations NOR any outbox item
    // persisted - proven by reading back from a completely separate DbContext/connection afterward, never
    // from the same in-memory instance the failed call used.
    [Fact]
    public async Task CancelBookingCommandHandler_WhenSaveChangesFailsBeforeCommit_PersistsNeitherTheCancellationsNorAnyOutboxItem()
    {
        var resourceId = await SeedResourceAsync(capacity: 8, requiresApproval: false);
        var seriesId = await SeedSeriesAsync(resourceId);
        var anchor = DateTimeOffset.UtcNow.AddDays(3);
        var primary = await SeedConfirmedOccurrenceAsync(resourceId, seriesId, anchor);
        var sibling = await SeedConfirmedOccurrenceAsync(resourceId, seriesId, anchor.AddDays(1));

        var throwingInterceptor = new ThrowingSaveChangesInterceptor { ShouldThrow = true };
        await using var dbContext = CreateDbContext(_tenantId, _ownerId, throwingInterceptor);
        var handler = CreateCancelHandler(dbContext);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(new CancelBookingCommandRequest(primary, CancelRemainingSeries: true), CancellationToken.None));

        await using var verify = CreateDbContext(_tenantId);
        Assert.Equal(BookingStatus.Confirmed, (await verify.Bookings.SingleAsync(b => b.Id == primary)).Status);
        Assert.Equal(BookingStatus.Confirmed, (await verify.Bookings.SingleAsync(b => b.Id == sibling)).Status);
        Assert.Equal(0, await verify.NotificationOutboxItems.IgnoreQueryFilters()
            .CountAsync(i => i.IdempotencyKey == $"booking:{primary}:cancellation" || i.IdempotencyKey == $"booking:{sibling}:cancellation"));
    }

    // Same failure-injection proof for a handler that DOES run inside IResourceBookingLock's explicit
    // transaction (BeginTransactionAsync/CommitAsync) - the lock's own catch-all rollback must discard the
    // staged series/bookings exactly as cleanly as the no-transaction Cancel case above does via plain
    // SaveChangesAsync failure.
    [Fact]
    public async Task CreateRecurringSeriesCommandHandler_WhenSaveChangesFailsBeforeCommit_PersistsNeitherTheSeriesNorAnyOutboxItem()
    {
        var resourceId = await SeedResourceAsync(capacity: 8, requiresApproval: false);
        var startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));

        var throwingInterceptor = new ThrowingSaveChangesInterceptor { ShouldThrow = true };
        await using var dbContext = CreateDbContext(_tenantId, _ownerId, throwingInterceptor);
        var handler = CreateSeriesHandler(dbContext);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(
            new CreateRecurringSeriesCommandRequest(resourceId, startDate, new TimeOnly(9, 0), new TimeOnly(10, 0),
                RecurrenceFrequency.Daily, Interval: 1, EndDate: null, OccurrenceCount: 3, Quantity: 1),
            CancellationToken.None));

        await using var verify = CreateDbContext(_tenantId);
        Assert.Equal(0, await verify.RecurringSeries.CountAsync(s => s.ResourceId == resourceId));
        Assert.Equal(0, await verify.Bookings.CountAsync(b => b.ResourceId == resourceId));
        Assert.Equal(0, await verify.NotificationOutboxItems.IgnoreQueryFilters().CountAsync());
    }

    // Test category 7: the whole point of EnqueueManyAsync is ONE SaveChangesAsync for the entire cascade,
    // not one per occurrence - proven by actually counting SavingChangesAsync invocations, not inferring it
    // from timing or behavior.
    [Fact]
    public async Task CancelBookingCommandHandler_WithCancelRemainingSeriesAcrossFiveOccurrences_IssuesExactlyOneSaveChangesCall()
    {
        var resourceId = await SeedResourceAsync(capacity: 8, requiresApproval: false);
        var seriesId = await SeedSeriesAsync(resourceId);
        var anchor = DateTimeOffset.UtcNow.AddDays(3);
        var primary = await SeedConfirmedOccurrenceAsync(resourceId, seriesId, anchor);
        for (var i = 1; i <= 4; i++)
        {
            await SeedConfirmedOccurrenceAsync(resourceId, seriesId, anchor.AddDays(i));
        }

        var countingInterceptor = new CountingSaveChangesInterceptor();
        await using var dbContext = CreateDbContext(_tenantId, _ownerId, countingInterceptor);
        var handler = CreateCancelHandler(dbContext);

        var result = await handler.Handle(new CancelBookingCommandRequest(primary, CancelRemainingSeries: true), CancellationToken.None);

        Assert.Equal(4, result.CascadedOccurrenceIds.Count); // primary + 4 cascaded = 5 occurrences total
        Assert.Equal(1, countingInterceptor.SaveChangesCallCount); // not 5 - one combined commit for the whole cascade
    }

    private CreateRecurringSeriesCommandHandler CreateSeriesHandler(BookSpaceDbContext dbContext) => new(
        new ResourceBookingLock(dbContext, NullLogger<ResourceBookingLock>.Instance),
        new ResourceRepository(dbContext),
        new AvailabilityRuleRepository(dbContext),
        new BlackoutPeriodRepository(dbContext),
        new BookingAvailabilityRepository(dbContext),
        new BookingRepository(dbContext),
        new RecurringSeriesRepository(dbContext),
        new ResourceApproverRepository(dbContext),
        new ApprovalRequestRepository(dbContext),
        new TenantRepository(dbContext),
        new FixedCurrentUserContext(_tenantId, _ownerId),
        new NotificationOutboxWriter(dbContext, NullLogger<NotificationOutboxWriter>.Instance));

    private ApproveBookingCommandHandler CreateApproveHandler(BookSpaceDbContext dbContext) => new(
        new ResourceBookingLock(dbContext, NullLogger<ResourceBookingLock>.Instance),
        new BookingRepository(dbContext),
        new ResourceRepository(dbContext),
        new AvailabilityRuleRepository(dbContext),
        new BlackoutPeriodRepository(dbContext),
        new BookingAvailabilityRepository(dbContext),
        new ApprovalRequestRepository(dbContext),
        new ResourceApproverRepository(dbContext),
        new FixedCurrentUserContext(_tenantId, _approverId),
        new NotificationOutboxWriter(dbContext, NullLogger<NotificationOutboxWriter>.Instance));

    private CancelBookingCommandHandler CreateCancelHandler(BookSpaceDbContext dbContext) => new(
        new BookingRepository(dbContext),
        new FixedCurrentUserContext(_tenantId, _ownerId),
        new NotificationOutboxWriter(dbContext, NullLogger<NotificationOutboxWriter>.Instance));

    private async Task<Guid> SeedResourceAsync(int capacity, bool requiresApproval)
    {
        var resourceId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(_tenantId);
        dbContext.Resources.Add(new Resource
        {
            Id = resourceId, TenantId = _tenantId, ResourceTypeId = _resourceTypeId, Name = $"Cascade Atomicity Room {resourceId:N}",
            Capacity = capacity, RequiresApproval = requiresApproval, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        });
        dbContext.AvailabilityRules.AddRange(Enum.GetValues<DayOfWeek>().Select(dayOfWeek => new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = resourceId,
            DayOfWeek = dayOfWeek, StartTime = TimeOnly.MinValue, EndTime = TimeOnly.MaxValue,
        }));
        if (requiresApproval)
        {
            dbContext.ResourceApprovers.Add(new ResourceApprover { Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = resourceId, UserId = _approverId });
        }

        await dbContext.SaveChangesAsync();
        return resourceId;
    }

    // Bookings.SeriesId carries a real foreign key to RecurringSeries - a cascaded occurrence can't be
    // seeded under a seriesId that doesn't actually exist as a row.
    private async Task<Guid> SeedSeriesAsync(Guid resourceId)
    {
        var seriesId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(_tenantId);
        dbContext.RecurringSeries.Add(new RecurringSeries
        {
            Id = seriesId, TenantId = _tenantId, ResourceId = resourceId, UserId = _ownerId,
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow), StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0),
            TimeZoneId = "UTC", Frequency = RecurrenceFrequency.Daily, Interval = 1, OccurrenceCount = 5, Quantity = 1,
        });
        await dbContext.SaveChangesAsync();
        return seriesId;
    }

    private async Task<Guid> SeedPendingOccurrenceAsync(Guid resourceId, Guid seriesId, DateTimeOffset start)
    {
        var bookingId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(_tenantId);
        dbContext.Bookings.Add(new Booking
        {
            Id = bookingId, TenantId = _tenantId, ResourceId = resourceId, UserId = _ownerId, SeriesId = seriesId,
            StartUtc = start, EndUtc = start.AddHours(1), Quantity = 1, Status = BookingStatus.Pending, CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        dbContext.ApprovalRequests.Add(new ApprovalRequest
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, BookingId = bookingId, ApproverId = null,
            Status = ApprovalStatus.Pending, RequestedAtUtc = DateTimeOffset.UtcNow, ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(48),
        });
        await dbContext.SaveChangesAsync();
        return bookingId;
    }

    private async Task<Guid> SeedConfirmedOccurrenceAsync(Guid resourceId, Guid seriesId, DateTimeOffset start)
    {
        var bookingId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(_tenantId);
        dbContext.Bookings.Add(new Booking
        {
            Id = bookingId, TenantId = _tenantId, ResourceId = resourceId, UserId = _ownerId, SeriesId = seriesId,
            StartUtc = start, EndUtc = start.AddHours(1), Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync();
        return bookingId;
    }

    private BookSpaceDbContext CreateDbContext(Guid tenantId, Guid? userId = null, params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString);
        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }

        return new BookSpaceDbContext(builder.Options, new FixedCurrentUserContext(tenantId, userId));
    }

    private sealed class FixedCurrentUserContext(Guid tenantId, Guid? userId = null) : ICurrentUserContext
    {
        public Guid? UserId => userId;
        public Guid? TenantId => tenantId;
        public IReadOnlyCollection<string> Roles => [];
    }

    // Throws right before SaveChanges actually sends anything to the database - simulates "the commit
    // itself never completes" without faking atomicity via a mock: this exercises the SAME real
    // SaveChangesAsync/transaction machinery every other test in this file does, just failing at the one
    // point that matters.
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

    private sealed class CountingSaveChangesInterceptor : SaveChangesInterceptor
    {
        public int SaveChangesCallCount { get; private set; }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            SaveChangesCallCount++;
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            SaveChangesCallCount++;
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
