using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Proves the DB-level CHECK constraints, unique indexes, and FK delete-behaviors declared in
// BookSpaceModelConfiguration.cs actually fire against a real SQL Server (LocalDB). Every one of these
// was previously verified only indirectly, through app-level FluentValidation/handler pre-checks (and
// only on SQLite in the fast integration suite, which cannot even translate a CHECK constraint the same
// way) - a migration or handler bug that skipped app validation would go completely undetected without
// tests that write directly to the DbContext, bypassing validators entirely, the way these do.
public sealed class SchemaConstraintTests : IAsyncLifetime
{
    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceSchemaConstraintTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    private Guid _tenantId;
    private Guid _resourceTypeId;
    private Guid _resourceId;
    private Guid _userId;

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureCreatedAsync();

        _tenantId = Guid.NewGuid();
        _resourceTypeId = Guid.NewGuid();
        _resourceId = Guid.NewGuid();
        _userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        dbContext.Tenants.Add(new Tenant { Id = _tenantId, Name = "Schema Constraint Tenant", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });
        dbContext.ResourceTypes.Add(new ResourceType { Id = _resourceTypeId, TenantId = _tenantId, Name = "Meeting Room" });
        dbContext.Users.Add(new User { Id = _userId, TenantId = _tenantId, FirstName = "Schema", LastName = "Test", Email = "schema@constraint.test", PasswordHash = "x", CreatedAtUtc = now });
        dbContext.Resources.Add(new Resource
        {
            Id = _resourceId, TenantId = _tenantId, ResourceTypeId = _resourceTypeId, Name = "Constraint Test Room",
            Capacity = 4, RequiresApproval = false, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        });
        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureDeletedAsync();
    }

    // --- CHECK constraints ---

    [Fact]
    public async Task InsertingAResourceWithZeroCapacity_ViolatesTheCapacityCheckConstraint()
    {
        await using var dbContext = CreateDbContext();
        dbContext.Resources.Add(new Resource
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ResourceTypeId = _resourceTypeId, Name = $"Bad {Guid.NewGuid()}",
            Capacity = 0, RequiresApproval = false, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task InsertingAnAvailabilityRuleWithEndTimeNotAfterStartTime_ViolatesTheTimeRangeCheckConstraint()
    {
        await using var dbContext = CreateDbContext();
        dbContext.AvailabilityRules.Add(new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = _resourceId,
            DayOfWeek = DayOfWeek.Monday, StartTime = new TimeOnly(10, 0), EndTime = new TimeOnly(10, 0),
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task InsertingABlackoutPeriodWithEndNotAfterStart_ViolatesTheTimeRangeCheckConstraint()
    {
        await using var dbContext = CreateDbContext();
        var start = DateTimeOffset.UtcNow.AddDays(1);
        dbContext.BlackoutPeriods.Add(new BlackoutPeriod
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = _resourceId, StartUtc = start, EndUtc = start, Reason = "Bad",
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task InsertingABookingWithEndNotAfterStart_ViolatesTheTimeRangeCheckConstraint()
    {
        await using var dbContext = CreateDbContext();
        var start = DateTimeOffset.UtcNow.AddDays(1);
        dbContext.Bookings.Add(new Booking
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = _resourceId, UserId = _userId,
            StartUtc = start, EndUtc = start, Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task InsertingABookingWithZeroQuantity_ViolatesTheQuantityCheckConstraint()
    {
        await using var dbContext = CreateDbContext();
        var start = DateTimeOffset.UtcNow.AddDays(1);
        dbContext.Bookings.Add(new Booking
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = _resourceId, UserId = _userId,
            StartUtc = start, EndUtc = start.AddHours(1), Quantity = 0, Status = BookingStatus.Confirmed, CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task InsertingARecurringSeriesWithZeroInterval_ViolatesTheIntervalCheckConstraint()
    {
        await using var dbContext = CreateDbContext();
        var series = ValidSeries();
        series.Interval = 0;
        dbContext.RecurringSeries.Add(series);

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task InsertingARecurringSeriesWithEndTimeNotAfterStartTime_ViolatesTheTimeRangeCheckConstraint()
    {
        await using var dbContext = CreateDbContext();
        var series = ValidSeries();
        series.EndTime = series.StartTime;
        dbContext.RecurringSeries.Add(series);

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task InsertingARecurringSeriesWithZeroQuantity_ViolatesTheQuantityCheckConstraint()
    {
        await using var dbContext = CreateDbContext();
        var series = ValidSeries();
        series.Quantity = 0;
        dbContext.RecurringSeries.Add(series);

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task InsertingARecurringSeriesWithZeroOccurrenceCount_ViolatesTheOccurrenceCountCheckConstraint()
    {
        await using var dbContext = CreateDbContext();
        var series = ValidSeries();
        series.OccurrenceCount = 0;
        dbContext.RecurringSeries.Add(series);

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task InsertingARecurringSeriesWithBothEndDateAndOccurrenceCount_ViolatesTheEndConditionCheckConstraint()
    {
        await using var dbContext = CreateDbContext();
        var series = ValidSeries(); // already has OccurrenceCount set
        series.EndDate = series.StartDate.AddMonths(1);
        dbContext.RecurringSeries.Add(series);

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task InsertingARecurringSeriesWithNeitherEndDateNorOccurrenceCount_ViolatesTheEndConditionCheckConstraint()
    {
        await using var dbContext = CreateDbContext();
        var series = ValidSeries();
        series.OccurrenceCount = null; // EndDate already null - now neither is set
        dbContext.RecurringSeries.Add(series);

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task InsertingAnApprovalRequestWithExpiryNotAfterRequestedAt_ViolatesTheExpiryCheckConstraint()
    {
        await using var dbContext = CreateDbContext();
        var bookingId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddDays(1);
        dbContext.Bookings.Add(new Booking
        {
            Id = bookingId, TenantId = _tenantId, ResourceId = _resourceId, UserId = _userId,
            StartUtc = start, EndUtc = start.AddHours(1), Quantity = 1, Status = BookingStatus.Pending, CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        var requestedAt = DateTimeOffset.UtcNow;
        dbContext.ApprovalRequests.Add(new ApprovalRequest
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, BookingId = bookingId, ApproverId = null,
            Status = ApprovalStatus.Pending, RequestedAtUtc = requestedAt, ExpiresAtUtc = requestedAt,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task InsertingATenantWithNegativeReminderLeadMinutes_ViolatesTheReminderLeadMinutesCheckConstraint()
    {
        await using var dbContext = CreateDbContext();
        dbContext.Tenants.Add(new Tenant
        {
            Id = Guid.NewGuid(), Name = $"Bad Tenant {Guid.NewGuid()}", DefaultTimeZoneId = "UTC",
            Status = TenantStatus.Active, CreatedAtUtc = DateTimeOffset.UtcNow, ReminderLeadMinutes = -1,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task InsertingATenantWithNegativeNoShowGraceMinutes_ViolatesTheNoShowGraceMinutesCheckConstraint()
    {
        await using var dbContext = CreateDbContext();
        dbContext.Tenants.Add(new Tenant
        {
            Id = Guid.NewGuid(), Name = $"Bad Tenant {Guid.NewGuid()}", DefaultTimeZoneId = "UTC",
            Status = TenantStatus.Active, CreatedAtUtc = DateTimeOffset.UtcNow, NoShowGraceMinutes = -1,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task InsertingATenantWithZeroApprovalExpiryHours_ViolatesTheApprovalExpiryHoursCheckConstraint()
    {
        await using var dbContext = CreateDbContext();
        dbContext.Tenants.Add(new Tenant
        {
            Id = Guid.NewGuid(), Name = $"Bad Tenant {Guid.NewGuid()}", DefaultTimeZoneId = "UTC",
            Status = TenantStatus.Active, CreatedAtUtc = DateTimeOffset.UtcNow, ApprovalExpiryHours = 0,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    // --- Unique indexes (DB-level backstop, distinct from the app-level pre-check every handler also
    // does - only Resources.(TenantId,Name) had a real-DB race test before this file). ---

    [Fact]
    public async Task InsertingADuplicateAvailabilityRule_ViolatesTheUniqueIndex()
    {
        await using var firstContext = CreateDbContext();
        firstContext.AvailabilityRules.Add(new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = _resourceId,
            DayOfWeek = DayOfWeek.Tuesday, StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(9, 0),
        });
        await firstContext.SaveChangesAsync();

        await using var secondContext = CreateDbContext();
        secondContext.AvailabilityRules.Add(new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = _resourceId,
            DayOfWeek = DayOfWeek.Tuesday, StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(9, 0),
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => secondContext.SaveChangesAsync());
    }

    [Fact]
    public async Task InsertingADuplicateResourceApprover_ViolatesTheUniqueIndex()
    {
        await using var firstContext = CreateDbContext();
        firstContext.ResourceApprovers.Add(new ResourceApprover { Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = _resourceId, UserId = _userId });
        await firstContext.SaveChangesAsync();

        await using var secondContext = CreateDbContext();
        secondContext.ResourceApprovers.Add(new ResourceApprover { Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = _resourceId, UserId = _userId });

        await Assert.ThrowsAsync<DbUpdateException>(() => secondContext.SaveChangesAsync());
    }

    [Fact]
    public async Task InsertingADuplicateResourceTypeName_ViolatesTheUniqueIndex()
    {
        var name = $"Duplicate Type {Guid.NewGuid()}";
        await using var firstContext = CreateDbContext();
        firstContext.ResourceTypes.Add(new ResourceType { Id = Guid.NewGuid(), TenantId = _tenantId, Name = name });
        await firstContext.SaveChangesAsync();

        await using var secondContext = CreateDbContext();
        secondContext.ResourceTypes.Add(new ResourceType { Id = Guid.NewGuid(), TenantId = _tenantId, Name = name });

        await Assert.ThrowsAsync<DbUpdateException>(() => secondContext.SaveChangesAsync());
    }

    [Fact]
    public async Task InsertingASecondApprovalRequestForTheSameBooking_ViolatesTheUniqueIndex()
    {
        var bookingId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddDays(1);
        await using var firstContext = CreateDbContext();
        firstContext.Bookings.Add(new Booking
        {
            Id = bookingId, TenantId = _tenantId, ResourceId = _resourceId, UserId = _userId,
            StartUtc = start, EndUtc = start.AddHours(1), Quantity = 1, Status = BookingStatus.Pending, CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        var now = DateTimeOffset.UtcNow;
        firstContext.ApprovalRequests.Add(new ApprovalRequest
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, BookingId = bookingId, ApproverId = null,
            Status = ApprovalStatus.Pending, RequestedAtUtc = now, ExpiresAtUtc = now.AddHours(48),
        });
        await firstContext.SaveChangesAsync();

        await using var secondContext = CreateDbContext();
        secondContext.ApprovalRequests.Add(new ApprovalRequest
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, BookingId = bookingId, ApproverId = null,
            Status = ApprovalStatus.Pending, RequestedAtUtc = now, ExpiresAtUtc = now.AddHours(48),
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => secondContext.SaveChangesAsync());
    }

    [Fact]
    public async Task InsertingADuplicateRefreshTokenHash_ViolatesTheUniqueIndex()
    {
        var tokenHash = $"hash-{Guid.NewGuid()}";
        await using var firstContext = CreateDbContext();
        firstContext.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(), UserId = _userId, FamilyId = Guid.NewGuid(), TokenHash = tokenHash,
            CreatedAtUtc = DateTimeOffset.UtcNow, ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(30),
        });
        await firstContext.SaveChangesAsync();

        await using var secondContext = CreateDbContext();
        secondContext.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(), UserId = _userId, FamilyId = Guid.NewGuid(), TokenHash = tokenHash,
            CreatedAtUtc = DateTimeOffset.UtcNow, ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(30),
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => secondContext.SaveChangesAsync());
    }

    [Fact]
    public async Task InsertingADuplicateTenantName_ViolatesTheUniqueIndex()
    {
        var name = $"Duplicate Tenant {Guid.NewGuid()}";
        await using var firstContext = CreateDbContext();
        firstContext.Tenants.Add(new Tenant { Id = Guid.NewGuid(), Name = name, DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = DateTimeOffset.UtcNow });
        await firstContext.SaveChangesAsync();

        await using var secondContext = CreateDbContext();
        secondContext.Tenants.Add(new Tenant { Id = Guid.NewGuid(), Name = name, DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = DateTimeOffset.UtcNow });

        await Assert.ThrowsAsync<DbUpdateException>(() => secondContext.SaveChangesAsync());
    }

    [Fact]
    public async Task InsertingADuplicateRoleName_ViolatesTheUniqueIndex()
    {
        var name = $"Duplicate Role {Guid.NewGuid()}";
        await using var firstContext = CreateDbContext();
        firstContext.Roles.Add(new Role { Id = Guid.NewGuid(), Name = name });
        await firstContext.SaveChangesAsync();

        await using var secondContext = CreateDbContext();
        secondContext.Roles.Add(new Role { Id = Guid.NewGuid(), Name = name });

        await Assert.ThrowsAsync<DbUpdateException>(() => secondContext.SaveChangesAsync());
    }

    [Fact]
    public async Task InsertingADuplicateUserRoleAssignment_ViolatesTheUniqueIndex()
    {
        await using var firstContext = CreateDbContext();
        var role = new Role { Id = Guid.NewGuid(), Name = $"Schema Role {Guid.NewGuid()}" };
        firstContext.Roles.Add(role);
        firstContext.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = _userId, RoleId = role.Id });
        await firstContext.SaveChangesAsync();

        await using var secondContext = CreateDbContext();
        secondContext.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = _userId, RoleId = role.Id });

        await Assert.ThrowsAsync<DbUpdateException>(() => secondContext.SaveChangesAsync());
    }

    // Proves the "global, not per-tenant" claim in BookSpaceModelConfiguration.cs's own comment - two
    // DIFFERENT tenants still cannot share an email, not just two users within the same tenant.
    [Fact]
    public async Task InsertingAUserWithAnEmailAlreadyUsedInAnotherTenant_ViolatesTheGlobalUniqueIndex()
    {
        var email = $"cross-tenant-{Guid.NewGuid()}@constraint.test";
        await using var firstContext = CreateDbContext();
        firstContext.Users.Add(new User { Id = Guid.NewGuid(), TenantId = _tenantId, FirstName = "First", LastName = "User", Email = email, PasswordHash = "x", CreatedAtUtc = DateTimeOffset.UtcNow });
        await firstContext.SaveChangesAsync();

        var otherTenantId = Guid.NewGuid();
        await using var secondContext = CreateDbContext();
        secondContext.Tenants.Add(new Tenant { Id = otherTenantId, Name = $"Other Tenant {Guid.NewGuid()}", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = DateTimeOffset.UtcNow });
        secondContext.Users.Add(new User { Id = Guid.NewGuid(), TenantId = otherTenantId, FirstName = "Second", LastName = "User", Email = email, PasswordHash = "x", CreatedAtUtc = DateTimeOffset.UtcNow });

        await Assert.ThrowsAsync<DbUpdateException>(() => secondContext.SaveChangesAsync());
    }

    // --- FK delete-behaviors ---

    [Fact]
    public async Task DeletingAResource_CascadesToItsAvailabilityRulesBlackoutPeriodsAndApprovers()
    {
        var resourceId = Guid.NewGuid();
        await using var seedContext = CreateDbContext();
        seedContext.Resources.Add(new Resource
        {
            Id = resourceId, TenantId = _tenantId, ResourceTypeId = _resourceTypeId, Name = $"Cascade {Guid.NewGuid()}",
            Capacity = 2, RequiresApproval = false, Status = ResourceStatus.Active, TimeZoneId = "UTC",
        });
        seedContext.AvailabilityRules.Add(new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = resourceId,
            DayOfWeek = DayOfWeek.Wednesday, StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(9, 0),
        });
        var start = DateTimeOffset.UtcNow.AddDays(1);
        seedContext.BlackoutPeriods.Add(new BlackoutPeriod { Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = resourceId, StartUtc = start, EndUtc = start.AddHours(1), Reason = "x" });
        seedContext.ResourceApprovers.Add(new ResourceApprover { Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = resourceId, UserId = _userId });
        await seedContext.SaveChangesAsync();

        await using var deleteContext = CreateDbContext();
        var resource = await deleteContext.Resources.SingleAsync(r => r.Id == resourceId);
        deleteContext.Resources.Remove(resource);
        await deleteContext.SaveChangesAsync();

        await using var verifyContext = CreateDbContext();
        Assert.False(await verifyContext.AvailabilityRules.AnyAsync(r => r.ResourceId == resourceId));
        Assert.False(await verifyContext.BlackoutPeriods.AnyAsync(b => b.ResourceId == resourceId));
        Assert.False(await verifyContext.ResourceApprovers.AnyAsync(a => a.ResourceId == resourceId));
    }

    [Fact]
    public async Task DeletingARecurringSeries_SetsNullOnItsBookingsSeriesIdRatherThanDeletingThem()
    {
        var seriesId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        await using var seedContext = CreateDbContext();
        var series = ValidSeries();
        series.Id = seriesId;
        seedContext.RecurringSeries.Add(series);
        var start = DateTimeOffset.UtcNow.AddDays(1);
        seedContext.Bookings.Add(new Booking
        {
            Id = bookingId, TenantId = _tenantId, ResourceId = _resourceId, UserId = _userId, SeriesId = seriesId,
            StartUtc = start, EndUtc = start.AddHours(1), Quantity = 1, Status = BookingStatus.Confirmed, CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await seedContext.SaveChangesAsync();

        await using var deleteContext = CreateDbContext();
        var seriesToDelete = await deleteContext.RecurringSeries.SingleAsync(s => s.Id == seriesId);
        deleteContext.RecurringSeries.Remove(seriesToDelete);
        await deleteContext.SaveChangesAsync();

        await using var verifyContext = CreateDbContext();
        var booking = await verifyContext.Bookings.SingleAsync(b => b.Id == bookingId);
        Assert.Null(booking.SeriesId); // the Booking row itself survives - only the FK is cleared
    }

    // The one Cascade relationship SchemaConstraintTests didn't yet cover - RefreshTokens.UserId is
    // configured Cascade (see BookSpaceModelConfiguration.cs), matching every other Cascade proven above,
    // but had no test verifying deleting a User actually removes their refresh tokens rather than
    // throwing an unexpected FK violation if a future migration ever drifted from the model.
    [Fact]
    public async Task DeletingAUser_CascadesToTheirRefreshTokens()
    {
        var userId = Guid.NewGuid();
        await using var seedContext = CreateDbContext();
        seedContext.Users.Add(new User
        {
            Id = userId, TenantId = _tenantId, FirstName = "Cascade", LastName = "Target",
            Email = $"cascade-{Guid.NewGuid():N}@constraint.test", PasswordHash = "x", CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        seedContext.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(), UserId = userId, FamilyId = Guid.NewGuid(), TokenHash = $"hash-{Guid.NewGuid()}",
            CreatedAtUtc = DateTimeOffset.UtcNow, ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(30),
        });
        await seedContext.SaveChangesAsync();

        await using var deleteContext = CreateDbContext();
        var user = await deleteContext.Users.SingleAsync(u => u.Id == userId);
        deleteContext.Users.Remove(user);
        await deleteContext.SaveChangesAsync();

        await using var verifyContext = CreateDbContext();
        Assert.False(await verifyContext.RefreshTokens.AnyAsync(t => t.UserId == userId));
    }

    // Complements the app-level DeleteResourceType_StillReferencedByAResource_ReturnsConflict API test -
    // this proves the DB-level Restrict FK itself fires, independent of that app-level pre-check.
    [Fact]
    public async Task DeletingAResourceTypeStillReferencedByAResource_ViolatesTheRestrictForeignKey()
    {
        await using var dbContext = CreateDbContext();
        var resourceType = await dbContext.ResourceTypes.SingleAsync(t => t.Id == _resourceTypeId);
        dbContext.ResourceTypes.Remove(resourceType);

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    private RecurringSeries ValidSeries() => new()
    {
        Id = Guid.NewGuid(), TenantId = _tenantId, ResourceId = _resourceId, UserId = _userId,
        StartDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(1)), StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0),
        TimeZoneId = "UTC", Frequency = RecurrenceFrequency.Daily, Interval = 1, OccurrenceCount = 5, Quantity = 1,
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
