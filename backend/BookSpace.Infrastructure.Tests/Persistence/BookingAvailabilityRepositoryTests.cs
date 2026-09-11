using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves GetActiveBookingsAsync's real SQL Server path (see the provider check in
// BookingAvailabilityRepository.cs) actually range-filters correctly, not just "doesn't throw" - the
// SQLite-backed Api.Tests suite can't prove this path at all, since SQLite can't translate the
// DateTimeOffset comparisons this query relies on. Mirrors the throwaway-database pattern used
// throughout this project's other real-SQL-Server tests.
public sealed class BookingAvailabilityRepositoryTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceBookingRangeTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantId;
    private Guid _resourceId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureCreatedAsync();

        _tenantId = Guid.NewGuid();
        var resourceTypeId = Guid.NewGuid();
        _resourceId = Guid.NewGuid();

        dbContext.Tenants.Add(new Tenant
        {
            Id = _tenantId, Name = "Booking Range Tenant", DefaultTimeZoneId = "UTC",
            Status = TenantStatus.Active, CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        dbContext.ResourceTypes.Add(new ResourceType { Id = resourceTypeId, TenantId = _tenantId, Name = "Meeting Room" });
        dbContext.Resources.Add(new Resource
        {
            Id = _resourceId, TenantId = _tenantId, ResourceTypeId = resourceTypeId, Name = "Range Test Room",
            Capacity = 4, RequiresApproval = false, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        });

        var userId = Guid.NewGuid();
        dbContext.Users.Add(new User
        {
            Id = userId, TenantId = _tenantId, FirstName = "Range", LastName = "User",
            Email = $"range-{Guid.NewGuid():N}@bookspace.test", PasswordHash = "irrelevant", CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        var queryWindowStart = new DateTimeOffset(2027, 6, 10, 0, 0, 0, TimeSpan.Zero);
        var queryWindowEnd = new DateTimeOffset(2027, 6, 17, 0, 0, 0, TimeSpan.Zero);

        dbContext.Bookings.AddRange(
            // Long before the query window - a resource with years of history must not force this
            // in, which is the whole point of filtering the range in SQL rather than fetching everything.
            NewBooking(userId, new DateTimeOffset(2020, 1, 1, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2020, 1, 1, 11, 0, 0, TimeSpan.Zero)),
            // Long after the query window.
            NewBooking(userId, new DateTimeOffset(2030, 1, 1, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2030, 1, 1, 11, 0, 0, TimeSpan.Zero)),
            // Fully inside the query window - must be returned.
            NewBooking(userId, queryWindowStart.AddDays(2), queryWindowStart.AddDays(2).AddHours(1)),
            // Straddles the window's start boundary (starts before, ends inside) - overlaps, must be returned.
            NewBooking(userId, queryWindowStart.AddHours(-1), queryWindowStart.AddHours(1)),
            // Entirely before the window, ends exactly at the window start - does not overlap, must be excluded.
            NewBooking(userId, queryWindowStart.AddHours(-2), queryWindowStart));

        await dbContext.SaveChangesAsync();

        _queryWindowStart = queryWindowStart;
        _queryWindowEnd = queryWindowEnd;
    }

    private DateTimeOffset _queryWindowStart;
    private DateTimeOffset _queryWindowEnd;

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task GetActiveBookingsAsync_OnRealSqlServer_ReturnsOnlyBookingsOverlappingTheRequestedRange()
    {
        await using var dbContext = CreateDbContext();
        var repository = new BookingAvailabilityRepository(dbContext);

        var results = await repository.GetActiveBookingsAsync(_resourceId, _queryWindowStart, _queryWindowEnd, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.All(results, booking => Assert.True(booking.StartUtc < _queryWindowEnd && booking.EndUtc > _queryWindowStart));
    }

    // The base seed above only proves the range filter at the window's START boundary (straddling and
    // touching-exactly cases) - the symmetric END boundary was never exercised.
    [Fact]
    public async Task GetActiveBookingsAsync_WithABookingStraddlingTheWindowEndBoundary_IncludesIt()
    {
        await using var seedContext = CreateDbContext();
        var userId = await seedContext.Users.Select(u => u.Id).FirstAsync();
        seedContext.Bookings.Add(NewBooking(userId, _queryWindowEnd.AddHours(-1), _queryWindowEnd.AddHours(1)));
        await seedContext.SaveChangesAsync();

        await using var dbContext = CreateDbContext();
        var repository = new BookingAvailabilityRepository(dbContext);

        var results = await repository.GetActiveBookingsAsync(_resourceId, _queryWindowStart, _queryWindowEnd, CancellationToken.None);

        Assert.Equal(3, results.Count); // the 2 from the base seed plus this straddling one
    }

    [Fact]
    public async Task GetActiveBookingsAsync_WithABookingStartingExactlyAtTheWindowEnd_ExcludesIt()
    {
        await using var seedContext = CreateDbContext();
        var userId = await seedContext.Users.Select(u => u.Id).FirstAsync();
        seedContext.Bookings.Add(NewBooking(userId, _queryWindowEnd, _queryWindowEnd.AddHours(1)));
        await seedContext.SaveChangesAsync();

        await using var dbContext = CreateDbContext();
        var repository = new BookingAvailabilityRepository(dbContext);

        var results = await repository.GetActiveBookingsAsync(_resourceId, _queryWindowStart, _queryWindowEnd, CancellationToken.None);

        Assert.Equal(2, results.Count); // unchanged from the base seed - touches but does not overlap
    }

    // ActiveStatuses (Pending/Confirmed count, everything else doesn't) was previously proven end-to-end
    // for Cancelled only (via BookingsEndpointsTests); Rejected/Completed/NoShow were never proven at this
    // repository level, or anywhere else.
    [Fact]
    public async Task GetActiveBookingsAsync_ExcludesRejectedCompletedAndNoShowBookings()
    {
        await using var seedContext = CreateDbContext();
        var userId = await seedContext.Users.Select(u => u.Id).FirstAsync();
        var insideWindow = _queryWindowStart.AddDays(3);
        foreach (var status in new[] { BookingStatus.Rejected, BookingStatus.Completed, BookingStatus.NoShow })
        {
            seedContext.Bookings.Add(new Booking
            {
                Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = _resourceId, UserId = userId,
                StartUtc = insideWindow, EndUtc = insideWindow.AddHours(1), Quantity = 1, Status = status, CreatedAtUtc = DateTimeOffset.UtcNow,
            });
        }
        await seedContext.SaveChangesAsync();

        await using var dbContext = CreateDbContext();
        var repository = new BookingAvailabilityRepository(dbContext);

        var results = await repository.GetActiveBookingsAsync(_resourceId, _queryWindowStart, _queryWindowEnd, CancellationToken.None);

        Assert.Equal(2, results.Count); // unchanged from the base seed - none of these three statuses count as active
    }

    private Booking NewBooking(Guid userId, DateTimeOffset startUtc, DateTimeOffset endUtc) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = _tenantId,
        ResourceId = _resourceId,
        UserId = userId,
        StartUtc = startUtc,
        EndUtc = endUtc,
        Quantity = 1,
        Status = BookingStatus.Confirmed,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private BookSpaceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString).Options;
        return new BookSpaceDbContext(options, new FixedCurrentUserContext(_tenantId));
    }

    private sealed class FixedCurrentUserContext(Guid tenantId) : ICurrentUserContext
    {
        public Guid? UserId => null;
        public Guid? TenantId => tenantId;
        public IReadOnlyCollection<string> Roles => [];
    }
}
