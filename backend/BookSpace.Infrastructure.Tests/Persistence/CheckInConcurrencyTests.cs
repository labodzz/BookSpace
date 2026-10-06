using BookSpace.Application.Bookings;
using BookSpace.Application.Common;
using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves CheckInBookingCommandHandler's use of IResourceBookingLock against real SQL Server (LocalDB) -
// required since SQLite has no UPDLOCK/HOLDLOCK support and the lock is a no-op there (same reasoning as
// ApprovalConcurrencyTests). Specifically proves the race this task's own design doc calls out: two
// concurrent check-in requests for the SAME booking must never both write CheckedInAtUtc - exactly one
// writes it, the other's fresh re-read (inside its own lock acquisition, which cannot start until the
// first's transaction fully commits) sees it already set and returns AlreadyCheckedIn with the SAME,
// unchanged instant.
public sealed class CheckInConcurrencyTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceCheckInConcurrencyTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantId;
    private Guid _resourceTypeId;
    private Guid _memberUserId;
    private Guid _approverUserId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext(Guid.NewGuid());
        await dbContext.Database.EnsureCreatedAsync();

        _tenantId = Guid.NewGuid();
        _resourceTypeId = Guid.NewGuid();
        _memberUserId = Guid.NewGuid();
        _approverUserId = Guid.NewGuid();

        var now = DateTimeOffset.UtcNow;
        dbContext.Tenants.Add(new Tenant
        {
            Id = _tenantId, Name = "CheckIn Concurrency Tenant", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active,
            CreatedAtUtc = now, CheckInLeadMinutes = 15, NoShowGraceMinutes = 15,
        });
        dbContext.ResourceTypes.Add(new ResourceType { Id = _resourceTypeId, TenantId = _tenantId, Name = "Meeting Room" });
        dbContext.Users.AddRange(
            new User { Id = _memberUserId, TenantId = _tenantId, FirstName = "Member", LastName = "User", Email = "member@checkin-concurrency.test", PasswordHash = "x", CreatedAtUtc = now },
            new User { Id = _approverUserId, TenantId = _tenantId, FirstName = "Approver", LastName = "User", Email = "approver@checkin-concurrency.test", PasswordHash = "x", CreatedAtUtc = now });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext(Guid.NewGuid());
        await dbContext.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task TwoConcurrentCheckInRequestsForTheSameBooking_ExactlyOneWritesAndTheOtherReportsAlreadyCheckedInWithTheSameInstant()
    {
        var resourceId = await SeedResourceAsync();
        var bookingId = await SeedConfirmedBookingWithinWindowAsync(resourceId, _memberUserId);

        using var startGate = new SemaphoreSlim(0, 2);

        async Task<CheckInBookingResponse> CheckInAsync()
        {
            await startGate.WaitAsync();
            await using var dbContext = CreateDbContext(_tenantId, _memberUserId);
            var handler = CreateHandler(dbContext, _memberUserId);
            return await handler.Handle(new CheckInBookingCommandRequest(bookingId), CancellationToken.None);
        }

        var taskA = Task.Run(CheckInAsync);
        var taskB = Task.Run(CheckInAsync);
        startGate.Release(2);
        var results = await Task.WhenAll(taskA, taskB);

        // Exactly one request actually performed the check-in; the other confirmed the existing one.
        Assert.Single(results, r => !r.AlreadyCheckedIn);
        Assert.Single(results, r => r.AlreadyCheckedIn);
        // Both responses report the SAME instant - the "already checked in" response never shows a
        // different (later, overwritten) time than the one that actually won.
        Assert.Equal(results[0].CheckedInAtUtc, results[1].CheckedInAtUtc);

        await using var verifyContext = CreateDbContext(_tenantId);
        var finalBooking = await verifyContext.Bookings.SingleAsync(b => b.Id == bookingId);
        Assert.Equal(results[0].CheckedInAtUtc, finalBooking.CheckedInAtUtc);
    }

    [Fact]
    public async Task ApproverAssignedToTheResource_CanCheckInAMembersBooking_ButAnUnassignedApproverCannot()
    {
        var resourceId = await SeedResourceAsync();
        var bookingId = await SeedConfirmedBookingWithinWindowAsync(resourceId, _memberUserId);
        await SeedResourceApproverAsync(resourceId, _approverUserId);
        var unassignedApproverUserId = Guid.NewGuid();
        await using (var seedContext = CreateDbContext(_tenantId))
        {
            seedContext.Users.Add(new User
            {
                Id = unassignedApproverUserId, TenantId = _tenantId, FirstName = "Unassigned", LastName = "Approver",
                Email = "unassigned@checkin-concurrency.test", PasswordHash = "x", CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            await seedContext.SaveChangesAsync();
        }

        await using var assignedContext = CreateDbContext(_tenantId, _approverUserId);
        var assignedResult = await CreateHandler(assignedContext, _approverUserId).Handle(new CheckInBookingCommandRequest(bookingId), CancellationToken.None);
        Assert.False(assignedResult.AlreadyCheckedIn);

        var secondBookingId = await SeedConfirmedBookingWithinWindowAsync(resourceId, _memberUserId);
        await using var unassignedContext = CreateDbContext(_tenantId, unassignedApproverUserId);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            CreateHandler(unassignedContext, unassignedApproverUserId).Handle(new CheckInBookingCommandRequest(secondBookingId), CancellationToken.None));
    }

    private async Task<Guid> SeedResourceAsync()
    {
        var resourceId = Guid.NewGuid();
        await using var dbContext = CreateDbContext(_tenantId);
        dbContext.Resources.Add(new Resource
        {
            Id = resourceId, TenantId = _tenantId, ResourceTypeId = _resourceTypeId, Name = $"CheckIn Room {resourceId:N}",
            Capacity = 4, RequiresApproval = false, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        });
        await dbContext.SaveChangesAsync();
        return resourceId;
    }

    private async Task SeedResourceApproverAsync(Guid resourceId, Guid approverUserId)
    {
        await using var dbContext = CreateDbContext(_tenantId);
        dbContext.ResourceApprovers.Add(new ResourceApprover { Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = resourceId, UserId = approverUserId });
        await dbContext.SaveChangesAsync();
    }

    private async Task<Guid> SeedConfirmedBookingWithinWindowAsync(Guid resourceId, Guid ownerUserId)
    {
        await using var dbContext = CreateDbContext(_tenantId);
        var start = DateTimeOffset.UtcNow; // window opens -15m/closes +15m from start - "now" is inside it
        var booking = new Booking
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = resourceId, UserId = ownerUserId,
            StartUtc = start, EndUtc = start.AddHours(1), Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = start.AddDays(-1),
        };
        dbContext.Bookings.Add(booking);
        await dbContext.SaveChangesAsync();
        return booking.Id;
    }

    private CheckInBookingCommandHandler CreateHandler(BookSpaceDbContext dbContext, Guid userId) => new(
        new ResourceBookingLock(dbContext, NullLogger<ResourceBookingLock>.Instance),
        new BookingRepository(dbContext),
        new TenantRepository(dbContext),
        new ResourceApproverRepository(dbContext),
        new FixedCurrentUserContext(_tenantId, userId));

    private BookSpaceDbContext CreateDbContext(Guid tenantId, Guid? userId = null)
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString).Options;
        return new BookSpaceDbContext(options, new FixedCurrentUserContext(tenantId, userId));
    }

    private sealed class FixedCurrentUserContext(Guid tenantId, Guid? userId = null) : ICurrentUserContext
    {
        public Guid? UserId => userId;
        public Guid? TenantId => tenantId;
        public IReadOnlyCollection<string> Roles => ["Approver"];
    }
}
