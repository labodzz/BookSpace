using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;

namespace BookSpace.Api.Tests;

// Minimal, self-contained dataset for the auth integration tests - a couple of tenants/users,
// independent from the Development-only DevelopmentSeeder (which is internal to Infrastructure
// and seeds a lot of booking data this suite doesn't need).
public static class TestDataSeeder
{
    public const string Password = "Test-Passw0rd!";

    public static readonly Guid AcmeTenantId = Guid.NewGuid();
    public static readonly Guid GlobexTenantId = Guid.NewGuid();

    public static readonly Guid AcmeAdminUserId = Guid.NewGuid();
    public const string AcmeAdminEmail = "admin@acme.integration-test";

    public static readonly Guid GlobexMemberUserId = Guid.NewGuid();
    public const string GlobexMemberEmail = "member@globex.integration-test";

    public static readonly Guid AcmeMemberUserId = Guid.NewGuid();
    public const string AcmeMemberEmail = "member@acme.integration-test";

    // Both roles exist in the domain (Roles table / [Authorize(Roles = "TenantAdmin,SysAdmin")]
    // everywhere in ResourcesController) but had no seeded user anywhere in this codebase - not in
    // TestDataSeeder, not even in the Development-only seeder - so neither role's authorization path
    // was ever actually exercised by a real login before these two were added.
    public static readonly Guid AcmeApproverUserId = Guid.NewGuid();
    public const string AcmeApproverEmail = "approver@acme.integration-test";

    public static readonly Guid AcmeSysAdminUserId = Guid.NewGuid();
    public const string AcmeSysAdminEmail = "sysadmin@acme.integration-test";

    public static readonly Guid ResourceTypeId = Guid.NewGuid();
    public static readonly Guid GlobexResourceTypeId = Guid.NewGuid();

    public static readonly Guid AcmeResourceId = Guid.NewGuid();
    public static readonly Guid GlobexResourceId = Guid.NewGuid();

    // A fixed point a few days out, rather than "today", so the seeded AvailabilityRules (which
    // cover every day of the week) never have to account for which weekday the suite happens to run on.
    // Built from DateTime.UtcNow.Date (Kind stays Utc) rather than DateTimeOffset.UtcNow.Date (whose
    // Kind is always Unspecified) - converting an Unspecified DateTime to DateTimeOffset silently
    // assumes the local machine's offset, not zero, which would shift this onto the wrong UTC day.
    public static readonly DateTimeOffset AvailabilityAnchorUtc = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(3);

    public static readonly DateTimeOffset AcmeBlackoutStartUtc = AvailabilityAnchorUtc.AddHours(10);
    public static readonly DateTimeOffset AcmeBlackoutEndUtc = AvailabilityAnchorUtc.AddHours(11);
    public const string AcmeBlackoutReason = "Integration test maintenance";

    public static readonly DateTimeOffset AcmeBookingStartUtc = AvailabilityAnchorUtc.AddHours(14);
    public static readonly DateTimeOffset AcmeBookingEndUtc = AvailabilityAnchorUtc.AddHours(16);
    public const int AcmeBookingQuantity = 3;

    public static readonly DateTimeOffset AcmeCancelledBookingStartUtc = AvailabilityAnchorUtc.AddHours(18);
    public static readonly DateTimeOffset AcmeCancelledBookingEndUtc = AvailabilityAnchorUtc.AddHours(19);

    // The remaining four BookingStatus values, so the full 6-status matrix has a seeded row of each -
    // only Pending and Confirmed (above) are supposed to block/consume capacity.
    public static readonly DateTimeOffset AcmePendingBookingStartUtc = AvailabilityAnchorUtc.AddHours(8);
    public static readonly DateTimeOffset AcmePendingBookingEndUtc = AvailabilityAnchorUtc.AddHours(9);
    public const int AcmePendingBookingQuantity = 2;

    public static readonly DateTimeOffset AcmeRejectedBookingStartUtc = AvailabilityAnchorUtc.AddHours(9);
    public static readonly DateTimeOffset AcmeRejectedBookingEndUtc = AvailabilityAnchorUtc.AddHours(10);

    public static readonly DateTimeOffset AcmeCompletedBookingStartUtc = AvailabilityAnchorUtc.AddHours(12);
    public static readonly DateTimeOffset AcmeCompletedBookingEndUtc = AvailabilityAnchorUtc.AddHours(13);

    public static readonly DateTimeOffset AcmeNoShowBookingStartUtc = AvailabilityAnchorUtc.AddHours(20);
    public static readonly DateTimeOffset AcmeNoShowBookingEndUtc = AvailabilityAnchorUtc.AddHours(21);

    public static async Task SeedAsync(BookSpaceDbContext dbContext, IPasswordHasher passwordHasher)
    {
        var passwordHash = passwordHasher.Hash(Password);
        var now = DateTimeOffset.UtcNow;

        var tenantAdminRole = new Role { Id = Guid.NewGuid(), Name = "TenantAdmin" };
        var memberRole = new Role { Id = Guid.NewGuid(), Name = "Member" };
        var approverRole = new Role { Id = Guid.NewGuid(), Name = "Approver" };
        var sysAdminRole = new Role { Id = Guid.NewGuid(), Name = "SysAdmin" };
        dbContext.Roles.AddRange(tenantAdminRole, memberRole, approverRole, sysAdminRole);

        dbContext.Tenants.AddRange(
            new Tenant { Id = AcmeTenantId, Name = "Acme Coworking", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now },
            new Tenant { Id = GlobexTenantId, Name = "Globex Labs", DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = now });

        dbContext.Users.AddRange(
            new User { Id = AcmeAdminUserId, TenantId = AcmeTenantId, FirstName = "Acme", LastName = "Admin", Email = AcmeAdminEmail, PasswordHash = passwordHash, CreatedAtUtc = now },
            new User { Id = AcmeMemberUserId, TenantId = AcmeTenantId, FirstName = "Acme", LastName = "Member", Email = AcmeMemberEmail, PasswordHash = passwordHash, CreatedAtUtc = now },
            new User { Id = AcmeApproverUserId, TenantId = AcmeTenantId, FirstName = "Acme", LastName = "Approver", Email = AcmeApproverEmail, PasswordHash = passwordHash, CreatedAtUtc = now },
            new User { Id = AcmeSysAdminUserId, TenantId = AcmeTenantId, FirstName = "Acme", LastName = "SysAdmin", Email = AcmeSysAdminEmail, PasswordHash = passwordHash, CreatedAtUtc = now },
            new User { Id = GlobexMemberUserId, TenantId = GlobexTenantId, FirstName = "Globex", LastName = "Member", Email = GlobexMemberEmail, PasswordHash = passwordHash, CreatedAtUtc = now });

        dbContext.UserRoles.AddRange(
            new UserRole { Id = Guid.NewGuid(), UserId = AcmeAdminUserId, RoleId = tenantAdminRole.Id },
            new UserRole { Id = Guid.NewGuid(), UserId = AcmeMemberUserId, RoleId = memberRole.Id },
            new UserRole { Id = Guid.NewGuid(), UserId = AcmeApproverUserId, RoleId = approverRole.Id },
            new UserRole { Id = Guid.NewGuid(), UserId = AcmeSysAdminUserId, RoleId = sysAdminRole.Id },
            new UserRole { Id = Guid.NewGuid(), UserId = GlobexMemberUserId, RoleId = memberRole.Id });

        dbContext.ResourceTypes.AddRange(
            new ResourceType { Id = ResourceTypeId, TenantId = AcmeTenantId, Name = "Meeting Room" },
            new ResourceType { Id = GlobexResourceTypeId, TenantId = GlobexTenantId, Name = "Meeting Room" });

        dbContext.Resources.AddRange(
            new Resource
            {
                Id = AcmeResourceId, TenantId = AcmeTenantId, ResourceTypeId = ResourceTypeId,
                Name = "Integration Test Room", Capacity = 8, RequiresApproval = false,
                Status = ResourceStatus.Active, TimeZoneId = "UTC",
            },
            // Deliberately bare - no rules/blackouts/bookings - so it only ever shows up in
            // Globex-scoped assertions, never in an Acme tenant-isolation assertion.
            new Resource
            {
                Id = GlobexResourceId, TenantId = GlobexTenantId, ResourceTypeId = GlobexResourceTypeId,
                Name = "Globex Only Room", Capacity = 4, RequiresApproval = false,
                Status = ResourceStatus.Active, TimeZoneId = "UTC",
            });

        // Whole-day rules for every weekday, so the fixture doesn't depend on which weekday the
        // suite happens to run on.
        dbContext.AvailabilityRules.AddRange(Enum.GetValues<DayOfWeek>().Select(dayOfWeek => new AvailabilityRule
        {
            Id = Guid.NewGuid(), TenantId = AcmeTenantId, ResourceId = AcmeResourceId,
            DayOfWeek = dayOfWeek, StartTime = TimeOnly.MinValue, EndTime = TimeOnly.MaxValue,
        }));

        dbContext.BlackoutPeriods.Add(new BlackoutPeriod
        {
            Id = Guid.NewGuid(), TenantId = AcmeTenantId, ResourceId = AcmeResourceId,
            StartUtc = AcmeBlackoutStartUtc, EndUtc = AcmeBlackoutEndUtc, Reason = AcmeBlackoutReason,
        });

        dbContext.Bookings.AddRange(
            new Booking
            {
                Id = Guid.NewGuid(), TenantId = AcmeTenantId, ResourceId = AcmeResourceId, UserId = AcmeMemberUserId,
                StartUtc = AcmeBookingStartUtc, EndUtc = AcmeBookingEndUtc, Quantity = AcmeBookingQuantity,
                Status = BookingStatus.Confirmed, CreatedAtUtc = now,
            },
            // Cancelled - must never show up as a busy period or reduce available capacity.
            new Booking
            {
                Id = Guid.NewGuid(), TenantId = AcmeTenantId, ResourceId = AcmeResourceId, UserId = AcmeMemberUserId,
                StartUtc = AcmeCancelledBookingStartUtc, EndUtc = AcmeCancelledBookingEndUtc, Quantity = 2,
                Status = BookingStatus.Cancelled, CreatedAtUtc = now,
            },
            // Pending - blocks capacity, same as Confirmed (both count as "active" per the business rule).
            new Booking
            {
                Id = Guid.NewGuid(), TenantId = AcmeTenantId, ResourceId = AcmeResourceId, UserId = AcmeMemberUserId,
                StartUtc = AcmePendingBookingStartUtc, EndUtc = AcmePendingBookingEndUtc, Quantity = AcmePendingBookingQuantity,
                Status = BookingStatus.Pending, CreatedAtUtc = now,
            },
            // Rejected/Completed/NoShow - like Cancelled, none of these are "active" and none should
            // show up as a busy period or reduce available capacity.
            new Booking
            {
                Id = Guid.NewGuid(), TenantId = AcmeTenantId, ResourceId = AcmeResourceId, UserId = AcmeMemberUserId,
                StartUtc = AcmeRejectedBookingStartUtc, EndUtc = AcmeRejectedBookingEndUtc, Quantity = 1,
                Status = BookingStatus.Rejected, CreatedAtUtc = now,
            },
            new Booking
            {
                Id = Guid.NewGuid(), TenantId = AcmeTenantId, ResourceId = AcmeResourceId, UserId = AcmeMemberUserId,
                StartUtc = AcmeCompletedBookingStartUtc, EndUtc = AcmeCompletedBookingEndUtc, Quantity = 1,
                Status = BookingStatus.Completed, CreatedAtUtc = now,
            },
            new Booking
            {
                Id = Guid.NewGuid(), TenantId = AcmeTenantId, ResourceId = AcmeResourceId, UserId = AcmeMemberUserId,
                StartUtc = AcmeNoShowBookingStartUtc, EndUtc = AcmeNoShowBookingEndUtc, Quantity = 1,
                Status = BookingStatus.NoShow, CreatedAtUtc = now,
            });

        await dbContext.SaveChangesAsync();
    }
}
