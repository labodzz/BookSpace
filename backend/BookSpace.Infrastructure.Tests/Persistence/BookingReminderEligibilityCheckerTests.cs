using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves BookingReminderEligibilityChecker's cancellation-race guard (docs/background-jobs.md,
// "Cancellation race for reminders") against a REAL SQL Server (LocalDB) - in particular the cross-tenant
// isolation claim, which requires a real tenant-scoped row to attempt to cross, not a mock.
public sealed class BookingReminderEligibilityCheckerTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceReminderEligibilityTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantAId;
    private Guid _tenantBId;
    private Guid _resourceTypeId;
    private Guid _resourceId;
    private Guid _userId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureCreatedAsync();

        _tenantAId = Guid.NewGuid();
        _tenantBId = Guid.NewGuid();
        _resourceTypeId = Guid.NewGuid();
        _resourceId = Guid.NewGuid();
        _userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        dbContext.Tenants.Add(new Tenant { Id = _tenantAId, Name = "Eligibility Tenant A", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.Tenants.Add(new Tenant { Id = _tenantBId, Name = "Eligibility Tenant B", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.ResourceTypes.Add(new ResourceType { Id = _resourceTypeId, TenantId = _tenantAId, Name = "Meeting Room" });
        dbContext.Resources.Add(new Resource
        {
            Id = _resourceId, TenantId = _tenantAId, ResourceTypeId = _resourceTypeId, Name = "Room",
            Capacity = 10, RequiresApproval = false, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        });
        dbContext.Users.Add(new User { Id = _userId, TenantId = _tenantAId, FirstName = "Eligibility", LastName = "User", Email = $"eligibility-{Guid.NewGuid():N}@bookspace.test", PasswordHash = "x", CreatedAtUtc = now });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task IsStillEligibleAsync_ForAConfirmedFutureBooking_ReturnsTrue()
    {
        var bookingId = await SeedBookingAsync(DateTimeOffset.UtcNow.AddMinutes(30), BookingStatus.Confirmed);

        var result = await CheckAsync(_tenantAId, bookingId);

        Assert.True(result);
    }

    [Fact]
    public async Task IsStillEligibleAsync_ForANonExistentBooking_ReturnsFalse()
    {
        var result = await CheckAsync(_tenantAId, Guid.NewGuid());

        Assert.False(result);
    }

    [Theory]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Rejected)]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.NoShow)]
    public async Task IsStillEligibleAsync_ForABookingThatIsNoLongerConfirmed_ReturnsFalse(BookingStatus status)
    {
        var bookingId = await SeedBookingAsync(DateTimeOffset.UtcNow.AddMinutes(30), status);

        var result = await CheckAsync(_tenantAId, bookingId);

        Assert.False(result);
    }

    [Fact]
    public async Task IsStillEligibleAsync_ForAConfirmedBookingThatHasAlreadyStarted_ReturnsFalse()
    {
        var bookingId = await SeedBookingAsync(DateTimeOffset.UtcNow.AddMinutes(-5), BookingStatus.Confirmed);

        var result = await CheckAsync(_tenantAId, bookingId);

        Assert.False(result);
    }

    // The core tenant requirement: a (tenantId, bookingId) pair that mismatches an otherwise-real,
    // Confirmed, future booking must never resolve - structurally, not merely "unlikely" - the same
    // guarantee IEmailRecipientResolver proves for (tenantId, userId).
    [Fact]
    public async Task IsStillEligibleAsync_WithTheBookingsRealIdButTheWrongTenant_ReturnsFalse()
    {
        var bookingId = await SeedBookingAsync(DateTimeOffset.UtcNow.AddMinutes(30), BookingStatus.Confirmed);

        var result = await CheckAsync(_tenantBId, bookingId);

        Assert.False(result);
    }

    private async Task<bool> CheckAsync(Guid tenantId, Guid bookingId)
    {
        await using var dbContext = CreateDbContext();
        var checker = new BookingReminderEligibilityChecker(dbContext, NullLogger<BookingReminderEligibilityChecker>.Instance);
        return await checker.IsStillEligibleAsync(tenantId, bookingId, CancellationToken.None);
    }

    private async Task<Guid> SeedBookingAsync(DateTimeOffset startUtc, BookingStatus status)
    {
        var id = Guid.NewGuid();
        await using var dbContext = CreateDbContext();
        dbContext.Bookings.Add(new Booking
        {
            Id = id,
            TenantId = _tenantAId,
            ResourceId = _resourceId,
            UserId = _userId,
            StartUtc = startUtc,
            EndUtc = startUtc.AddHours(1),
            Quantity = 1,
            Status = status,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync();
        return id;
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
}
