using BookSpace.Application.Bookings;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves BookingRepository.GetForCalendarFeedAsync's status mapping, tenant/user isolation (bypassing the
// ambient query filter exactly like the anonymous feed request would), time-window filtering, and
// never-just-one-page behavior against a REAL SQL Server (LocalDB) - see
// docs/calendar-subscription-feed.md ("Booking status mapping", "Time window", "Query and scaling").
public sealed class BookingRepositoryCalendarFeedTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceCalendarFeedBookingRepoTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantId;
    private Guid _otherTenantId;
    private Guid _resourceId;
    private Guid _otherTenantResourceId;
    private Guid _resourceTypeId;
    private Guid _userId;
    private Guid _otherUserId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureCreatedAsync();

        _tenantId = Guid.NewGuid();
        _otherTenantId = Guid.NewGuid();
        _resourceTypeId = Guid.NewGuid();
        _resourceId = Guid.NewGuid();
        _otherTenantResourceId = Guid.NewGuid();
        _userId = Guid.NewGuid();
        _otherUserId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        dbContext.Tenants.Add(new Tenant { Id = _tenantId, Name = "Feed Tenant", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.Tenants.Add(new Tenant { Id = _otherTenantId, Name = "Other Feed Tenant", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.ResourceTypes.Add(new ResourceType { Id = _resourceTypeId, TenantId = _tenantId, Name = "Room" });
        dbContext.Resources.Add(new Resource
        {
            Id = _resourceId, TenantId = _tenantId, ResourceTypeId = _resourceTypeId, Name = "Conference Room A",
            Capacity = 10, RequiresApproval = false, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        });
        dbContext.Resources.Add(new Resource
        {
            Id = _otherTenantResourceId, TenantId = _otherTenantId, ResourceTypeId = _resourceTypeId, Name = "Other Tenant Room",
            Capacity = 10, RequiresApproval = false, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        });
        dbContext.Users.Add(new User { Id = _userId, TenantId = _tenantId, FirstName = "Feed", LastName = "User", Email = $"feed-user-{Guid.NewGuid():N}@bookspace.test", PasswordHash = "x", CreatedAtUtc = now });
        dbContext.Users.Add(new User { Id = _otherUserId, TenantId = _tenantId, FirstName = "Other", LastName = "User", Email = $"other-user-{Guid.NewGuid():N}@bookspace.test", PasswordHash = "x", CreatedAtUtc = now });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureDeletedAsync();
    }

    [Theory]
    [InlineData(BookingStatus.Pending, false)]
    [InlineData(BookingStatus.Confirmed, true)]
    [InlineData(BookingStatus.Rejected, false)]
    [InlineData(BookingStatus.Cancelled, false)]
    [InlineData(BookingStatus.Completed, true)]
    [InlineData(BookingStatus.NoShow, false)]
    [InlineData(BookingStatus.Expired, false)]
    public async Task GetForCalendarFeedAsync_OnlyEverReturnsConfirmedOrCompletedBookings(BookingStatus status, bool expectedIncluded)
    {
        var bookingId = await SeedBookingAsync(_tenantId, _resourceId, _userId, DateTimeOffset.UtcNow.AddDays(1), status);

        var result = await GetFeedAsync(_tenantId, _userId);

        Assert.Equal(expectedIncluded, result.Any(row => row.Id == bookingId));
    }

    [Fact]
    public async Task GetForCalendarFeedAsync_NeverReturnsAnotherTenantsBookings_EvenWithNoAmbientTenantContext()
    {
        await SeedBookingAsync(_tenantId, _resourceId, _userId, DateTimeOffset.UtcNow.AddDays(1), BookingStatus.Confirmed);
        var otherTenantBookingId = await SeedBookingAsync(_otherTenantId, _otherTenantResourceId, Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1), BookingStatus.Confirmed, skipUserFk: true);

        var result = await GetFeedAsync(_tenantId, _userId);

        Assert.DoesNotContain(result, row => row.Id == otherTenantBookingId);
    }

    [Fact]
    public async Task GetForCalendarFeedAsync_NeverReturnsAnotherUsersBookingsInTheSameTenant()
    {
        var ownBookingId = await SeedBookingAsync(_tenantId, _resourceId, _userId, DateTimeOffset.UtcNow.AddDays(1), BookingStatus.Confirmed);
        var otherUsersBookingId = await SeedBookingAsync(_tenantId, _resourceId, _otherUserId, DateTimeOffset.UtcNow.AddDays(1), BookingStatus.Confirmed);

        var result = await GetFeedAsync(_tenantId, _userId);

        Assert.Contains(result, row => row.Id == ownBookingId);
        Assert.DoesNotContain(result, row => row.Id == otherUsersBookingId);
    }

    [Fact]
    public async Task GetForCalendarFeedAsync_JoinsTheResourcesOwnNameIntoEachRow()
    {
        await SeedBookingAsync(_tenantId, _resourceId, _userId, DateTimeOffset.UtcNow.AddDays(1), BookingStatus.Confirmed);

        var result = await GetFeedAsync(_tenantId, _userId);

        Assert.All(result, row => Assert.Equal("Conference Room A", row.ResourceName));
    }

    [Fact]
    public async Task GetForCalendarFeedAsync_ExcludesBookingsStartingAfterTheWindowEnd()
    {
        var farFutureId = await SeedBookingAsync(_tenantId, _resourceId, _userId, DateTimeOffset.UtcNow.AddDays(400), BookingStatus.Confirmed);

        var result = await GetFeedAsync(_tenantId, _userId, windowEndUtc: DateTimeOffset.UtcNow.AddDays(365));

        Assert.DoesNotContain(result, row => row.Id == farFutureId);
    }

    [Fact]
    public async Task GetForCalendarFeedAsync_ExcludesBookingsThatEndedBeforeTheWindowStart()
    {
        var start = DateTimeOffset.UtcNow.AddDays(-40);
        var farPastId = await SeedBookingAsync(_tenantId, _resourceId, _userId, start, BookingStatus.Confirmed);

        var result = await GetFeedAsync(_tenantId, _userId, windowStartUtc: DateTimeOffset.UtcNow.AddDays(-30));

        Assert.DoesNotContain(result, row => row.Id == farPastId);
    }

    [Fact]
    public async Task GetForCalendarFeedAsync_IncludesABookingOverlappingTheWindowBoundary()
    {
        var windowStart = DateTimeOffset.UtcNow;
        // Starts before the window but ends inside it - genuinely overlapping, not fully outside.
        var overlappingId = await SeedBookingWithExplicitRangeAsync(
            _tenantId, _resourceId, _userId, windowStart.AddHours(-1), windowStart.AddHours(1), BookingStatus.Confirmed);

        var result = await GetFeedAsync(_tenantId, _userId, windowStartUtc: windowStart, windowEndUtc: windowStart.AddDays(1));

        Assert.Contains(result, row => row.Id == overlappingId);
    }

    // Proves the feed never silently reuses "one page" of the ordinary GetOwnBookingsAsync shape (whose
    // default page size is 20) - every one of a batch comfortably larger than that single page must come
    // back, not just the first 20.
    [Fact]
    public async Task GetForCalendarFeedAsync_ReturnsEveryBooking_EvenFarMoreThanOnePageWorthOfTheOrdinaryEndpoint()
    {
        const int bookingCount = 35;
        var seededIds = new List<Guid>();
        for (var i = 0; i < bookingCount; i++)
        {
            seededIds.Add(await SeedBookingAsync(_tenantId, _resourceId, _userId, DateTimeOffset.UtcNow.AddDays(1).AddHours(i), BookingStatus.Confirmed));
        }

        var result = await GetFeedAsync(_tenantId, _userId);

        Assert.Equal(bookingCount, result.Count(row => seededIds.Contains(row.Id)));
    }

    [Fact]
    public async Task GetForCalendarFeedAsync_OrdersResultsByStartUtc()
    {
        var later = await SeedBookingAsync(_tenantId, _resourceId, _userId, DateTimeOffset.UtcNow.AddDays(3), BookingStatus.Confirmed);
        var earlier = await SeedBookingAsync(_tenantId, _resourceId, _userId, DateTimeOffset.UtcNow.AddDays(1), BookingStatus.Confirmed);

        var result = await GetFeedAsync(_tenantId, _userId);

        var earlierIndex = result.ToList().FindIndex(row => row.Id == earlier);
        var laterIndex = result.ToList().FindIndex(row => row.Id == later);
        Assert.True(earlierIndex < laterIndex);
    }

    private async Task<IReadOnlyList<CalendarFeedBooking>> GetFeedAsync(
        Guid tenantId, Guid userId, DateTimeOffset? windowStartUtc = null, DateTimeOffset? windowEndUtc = null)
    {
        await using var dbContext = CreateDbContext();
        var repository = new BookingRepository(dbContext);
        return await repository.GetForCalendarFeedAsync(
            tenantId, userId, windowStartUtc ?? DateTimeOffset.UtcNow.AddDays(-30), windowEndUtc ?? DateTimeOffset.UtcNow.AddDays(365), CancellationToken.None);
    }

    private Task<Guid> SeedBookingAsync(Guid tenantId, Guid resourceId, Guid userId, DateTimeOffset startUtc, BookingStatus status, bool skipUserFk = false) =>
        SeedBookingWithExplicitRangeAsync(tenantId, resourceId, userId, startUtc, startUtc.AddHours(1), status, skipUserFk);

    private async Task<Guid> SeedBookingWithExplicitRangeAsync(
        Guid tenantId, Guid resourceId, Guid userId, DateTimeOffset startUtc, DateTimeOffset endUtc, BookingStatus status, bool skipUserFk = false)
    {
        var bookingId = Guid.NewGuid();
        await using var dbContext = CreateDbContext();
        if (skipUserFk)
        {
            // A second tenant's own user isn't part of this fixture's seeded Users - add one inline so the
            // FK is satisfiable without widening InitializeAsync's shared fixture for a single test's needs.
            dbContext.Users.Add(new User { Id = userId, TenantId = tenantId, FirstName = "X", LastName = "Y", Email = $"x-{Guid.NewGuid():N}@bookspace.test", PasswordHash = "x", CreatedAtUtc = DateTimeOffset.UtcNow });
        }

        dbContext.Bookings.Add(new Booking
        {
            Id = bookingId, TenantId = tenantId, ResourceId = resourceId, UserId = userId,
            StartUtc = startUtc, EndUtc = endUtc, Quantity = 1, Status = status, CreatedAtUtc = startUtc.AddDays(-1),
        });
        await dbContext.SaveChangesAsync();
        return bookingId;
    }

    private BookSpaceDbContext CreateDbContext(Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString).Options;
        return new BookSpaceDbContext(options, new FixedCurrentUserContext(tenantId));
    }

    private sealed class FixedCurrentUserContext(Guid? tenantId = null) : ICurrentUserContext
    {
        public Guid? UserId => null;
        public Guid? TenantId => tenantId;
        public IReadOnlyCollection<string> Roles => [];
    }
}
