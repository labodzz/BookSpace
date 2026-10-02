using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using BookSpace.Application.Notifications;
using BookSpace.Application.Resources;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves, against real SQL Server (LocalDB), the invariant docs/recurring-bookings-and-approvals.md §7
// states but never had a real-database test for: "CreateRecurringSeriesCommandHandler uses the lock
// once, for the whole series" - the exact same IResourceBookingLock boundary CreateBookingCommandHandler
// uses (WP-4), so a series creation and a single-booking creation racing for the same resource/window
// must serialize exactly like two single-booking creations already proven to in BookingConcurrencyTests.
// Requires real SQL Server: SQLite has no UPDLOCK/HOLDLOCK support, so IResourceBookingLock is a no-op
// there and this race cannot be proven against it.
public sealed class RecurringSeriesConcurrencyTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceRecurringSeriesConcurrencyTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantId;
    private Guid _resourceTypeId;
    private Guid _seriesOwnerId;
    private Guid _singleBookingOwnerId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext(Guid.NewGuid());
        await dbContext.Database.EnsureCreatedAsync();

        _tenantId = Guid.NewGuid();
        _resourceTypeId = Guid.NewGuid();
        _seriesOwnerId = Guid.NewGuid();
        _singleBookingOwnerId = Guid.NewGuid();

        var now = DateTimeOffset.UtcNow;
        dbContext.Tenants.Add(new Tenant { Id = _tenantId, Name = "Recurring Series Concurrency Tenant", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.ResourceTypes.Add(new ResourceType { Id = _resourceTypeId, TenantId = _tenantId, Name = "Meeting Room" });
        dbContext.Users.Add(new User { Id = _seriesOwnerId, TenantId = _tenantId, FirstName = "Series", LastName = "Owner", Email = "series@recurring-concurrency.test", PasswordHash = "x", CreatedAtUtc = now });
        dbContext.Users.Add(new User { Id = _singleBookingOwnerId, TenantId = _tenantId, FirstName = "Single", LastName = "Owner", Email = "single@recurring-concurrency.test", PasswordHash = "x", CreatedAtUtc = now });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext(Guid.NewGuid());
        await dbContext.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task CreateRecurringSeriesCommandHandler_RacedAgainstCreateBookingCommandHandler_ForTheSameWindowAtCapacityOne_ExactlyOneSucceeds()
    {
        var resourceId = await SeedResourceAsync(capacity: 1);
        var startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        var start = new DateTimeOffset(startDate.Year, startDate.Month, startDate.Day, 9, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(startDate.Year, startDate.Month, startDate.Day, 10, 0, 0, TimeSpan.Zero);

        using var startGate = new SemaphoreSlim(0, 2);

        async Task<bool> CreateSeriesAsync()
        {
            await startGate.WaitAsync();
            await using var dbContext = CreateDbContext(_tenantId, _seriesOwnerId);
            var handler = CreateSeriesHandler(dbContext, _seriesOwnerId);
            try
            {
                await handler.Handle(
                    new CreateRecurringSeriesCommandRequest(
                        resourceId, startDate, new TimeOnly(9, 0), new TimeOnly(10, 0),
                        RecurrenceFrequency.Weekly, Interval: 1, EndDate: null, OccurrenceCount: 1, Quantity: 1),
                    CancellationToken.None);
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }

        async Task<bool> CreateSingleBookingAsync()
        {
            await startGate.WaitAsync();
            var handler = CreateBookingHandler(_singleBookingOwnerId, out var dbContextToDispose);
            try
            {
                await handler.Handle(new CreateBookingCommandRequest(resourceId, start, end, Quantity: 1), CancellationToken.None);
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
            finally
            {
                await dbContextToDispose.DisposeAsync();
            }
        }

        var seriesTask = Task.Run(CreateSeriesAsync);
        var bookingTask = Task.Run(CreateSingleBookingAsync);
        startGate.Release(2);
        var results = await Task.WhenAll(seriesTask, bookingTask);

        Assert.Single(results, succeeded => succeeded);
        Assert.Single(results, succeeded => !succeeded);

        await using var verifyContext = CreateDbContext(_tenantId);
        var committedDemand = await verifyContext.Bookings
            .Where(b => b.ResourceId == resourceId && (b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed))
            .SumAsync(b => b.Quantity);
        Assert.Equal(1, committedDemand); // exactly the allowed capacity - no double-booking from either creation path.
    }

    [Fact]
    public async Task TwoConcurrentCreateRecurringSeriesCommandHandlerCalls_ForOverlappingWindowsAtCapacityOne_ExactlyOneSucceeds()
    {
        var resourceId = await SeedResourceAsync(capacity: 1);
        var startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));

        using var startGate = new SemaphoreSlim(0, 2);

        async Task<bool> CreateSeriesAsync(Guid ownerId)
        {
            await startGate.WaitAsync();
            await using var dbContext = CreateDbContext(_tenantId, ownerId);
            var handler = CreateSeriesHandler(dbContext, ownerId);
            try
            {
                await handler.Handle(
                    new CreateRecurringSeriesCommandRequest(
                        resourceId, startDate, new TimeOnly(9, 0), new TimeOnly(10, 0),
                        RecurrenceFrequency.Weekly, Interval: 1, EndDate: null, OccurrenceCount: 2, Quantity: 1),
                    CancellationToken.None);
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }

        var taskA = Task.Run(() => CreateSeriesAsync(_seriesOwnerId));
        var taskB = Task.Run(() => CreateSeriesAsync(_singleBookingOwnerId));
        startGate.Release(2);
        var results = await Task.WhenAll(taskA, taskB);

        // Both series request the identical two occurrences for a capacity-1 resource - whichever
        // acquires the lock first fully commits both its occurrences before the other even reads current
        // state, so the second series' every occurrence correctly re-checks against the first's committed
        // bookings and conflicts on all of them (no eligible occurrence at all -> ConflictException).
        Assert.Single(results, succeeded => succeeded);
        Assert.Single(results, succeeded => !succeeded);

        await using var verifyContext = CreateDbContext(_tenantId);
        var seriesCount = await verifyContext.RecurringSeries.CountAsync(s => s.ResourceId == resourceId);
        Assert.Equal(1, seriesCount); // the losing series was never persisted at all - not even partially.
    }

    private async Task<Guid> SeedResourceAsync(int capacity)
    {
        var resourceId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(_tenantId);
        dbContext.Resources.Add(new Resource
        {
            Id = resourceId, TenantId = _tenantId, ResourceTypeId = _resourceTypeId, Name = $"Recurring Concurrency Room {resourceId:N}",
            Capacity = capacity, RequiresApproval = false, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        });
        dbContext.AvailabilityRules.AddRange(Enum.GetValues<DayOfWeek>().Select(dayOfWeek => new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = resourceId,
            DayOfWeek = dayOfWeek, StartTime = TimeOnly.MinValue, EndTime = TimeOnly.MaxValue,
        }));
        await dbContext.SaveChangesAsync();
        return resourceId;
    }

    private CreateRecurringSeriesCommandHandler CreateSeriesHandler(BookSpaceDbContext dbContext, Guid userId) => new(
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
        new FixedCurrentUserContext(_tenantId, userId),
        new NotificationOutboxWriter(dbContext, NullLogger<NotificationOutboxWriter>.Instance));

    // Each simulated request gets its own BookSpaceDbContext, exactly like two real concurrent HTTP
    // requests would - see BookingConcurrencyTests.CreateHandler for the same convention.
    private CreateBookingCommandHandler CreateBookingHandler(Guid userId, out BookSpaceDbContext dbContext)
    {
        dbContext = CreateDbContext(_tenantId, userId);
        return new CreateBookingCommandHandler(
            new ResourceBookingLock(dbContext, NullLogger<ResourceBookingLock>.Instance),
            new ResourceRepository(dbContext),
            new AvailabilityRuleRepository(dbContext),
            new BlackoutPeriodRepository(dbContext),
            new BookingAvailabilityRepository(dbContext),
            new BookingRepository(dbContext),
            new ResourceApproverRepository(dbContext),
            new ApprovalRequestRepository(dbContext),
            new TenantRepository(dbContext),
            new FixedCurrentUserContext(_tenantId, userId),
            new NotificationOutboxWriter(dbContext, NullLogger<NotificationOutboxWriter>.Instance));
    }

    private BookSpaceDbContext CreateDbContext(Guid tenantId, Guid? userId = null)
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString).Options;
        return new BookSpaceDbContext(options, new FixedCurrentUserContext(tenantId, userId));
    }

    private sealed class FixedCurrentUserContext(Guid tenantId, Guid? userId = null) : ICurrentUserContext
    {
        public Guid? UserId => userId;
        public Guid? TenantId => tenantId;
        public IReadOnlyCollection<string> Roles => [];
    }
}
