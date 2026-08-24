using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

// Seeds a small but realistic multi-tenant dataset for local development and the WP-1 design review -
// two tenants, roles, resources with availability, a blackout, a two-year weekly recurring series with
// a few materialized occurrences, and one pending approval.
internal static class DevelopmentSeeder
{
    public static async Task SeedAsync(BookSpaceDbContext dbContext, CancellationToken cancellationToken = default)
    {
        if (await dbContext.Tenants.AnyAsync(cancellationToken))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        var sysAdminRole = new Role { Id = Guid.NewGuid(), Name = "SysAdmin" };
        var tenantAdminRole = new Role { Id = Guid.NewGuid(), Name = "TenantAdmin" };
        var approverRole = new Role { Id = Guid.NewGuid(), Name = "Approver" };
        var memberRole = new Role { Id = Guid.NewGuid(), Name = "Member" };
        dbContext.Roles.AddRange(sysAdminRole, tenantAdminRole, approverRole, memberRole);

        var meetingRoomType = new ResourceType { Id = Guid.NewGuid(), Name = "Meeting Room" };
        var deskType = new ResourceType { Id = Guid.NewGuid(), Name = "Desk" };
        dbContext.ResourceTypes.AddRange(meetingRoomType, deskType);

        var acme = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Acme Coworking",
            DefaultTimeZoneId = "Europe/Sarajevo",
            Status = TenantStatus.Active,
            CreatedAtUtc = now,
        };
        var globex = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Globex Labs",
            DefaultTimeZoneId = "UTC",
            Status = TenantStatus.Active,
            CreatedAtUtc = now,
        };
        dbContext.Tenants.AddRange(acme, globex);

        var acmeAdmin = new User { Id = Guid.NewGuid(), TenantId = acme.Id, FirstName = "Amina", LastName = "Kovač", Email = "admin@acme.test", CreatedAtUtc = now };
        var acmeApprover = new User { Id = Guid.NewGuid(), TenantId = acme.Id, FirstName = "Emir", LastName = "Hodžić", Email = "approver@acme.test", CreatedAtUtc = now };
        var acmeMember = new User { Id = Guid.NewGuid(), TenantId = acme.Id, FirstName = "Lamija", LastName = "Bojić", Email = "member@acme.test", CreatedAtUtc = now };
        var globexAdmin = new User { Id = Guid.NewGuid(), TenantId = globex.Id, FirstName = "John", LastName = "Doe", Email = "admin@globex.test", CreatedAtUtc = now };
        var globexMember = new User { Id = Guid.NewGuid(), TenantId = globex.Id, FirstName = "Jane", LastName = "Smith", Email = "member@globex.test", CreatedAtUtc = now };
        dbContext.Users.AddRange(acmeAdmin, acmeApprover, acmeMember, globexAdmin, globexMember);

        dbContext.UserRoles.AddRange(
            new UserRole { Id = Guid.NewGuid(), UserId = acmeAdmin.Id, RoleId = tenantAdminRole.Id },
            new UserRole { Id = Guid.NewGuid(), UserId = acmeApprover.Id, RoleId = approverRole.Id },
            new UserRole { Id = Guid.NewGuid(), UserId = acmeMember.Id, RoleId = memberRole.Id },
            new UserRole { Id = Guid.NewGuid(), UserId = globexAdmin.Id, RoleId = tenantAdminRole.Id },
            new UserRole { Id = Guid.NewGuid(), UserId = globexMember.Id, RoleId = memberRole.Id });

        var conferenceRoomA = new Resource
        {
            Id = Guid.NewGuid(), TenantId = acme.Id, ResourceTypeId = meetingRoomType.Id,
            Name = "Conference Room A", Description = "8-seat room with a projector", Capacity = 8,
            RequiresApproval = true, Status = ResourceStatus.Active, TimeZoneId = acme.DefaultTimeZoneId,
        };
        var hotDesk1 = new Resource
        {
            Id = Guid.NewGuid(), TenantId = acme.Id, ResourceTypeId = deskType.Id,
            Name = "Hot Desk 1", Description = null, Capacity = 1,
            RequiresApproval = false, Status = ResourceStatus.Active, TimeZoneId = acme.DefaultTimeZoneId,
        };
        var warRoom = new Resource
        {
            Id = Guid.NewGuid(), TenantId = globex.Id, ResourceTypeId = meetingRoomType.Id,
            Name = "War Room", Description = "6-seat room", Capacity = 6,
            RequiresApproval = false, Status = ResourceStatus.Active, TimeZoneId = globex.DefaultTimeZoneId,
        };
        dbContext.Resources.AddRange(conferenceRoomA, hotDesk1, warRoom);

        var weekdayHours = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
        foreach (var resource in new[] { conferenceRoomA, hotDesk1, warRoom })
        {
            dbContext.AvailabilityRules.AddRange(weekdayHours.Select(day => new AvailabilityRule
            {
                Id = Guid.NewGuid(),
                TenantId = resource.TenantId,
                ResourceId = resource.Id,
                DayOfWeek = day,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(18, 0),
            }));
        }

        dbContext.BlackoutPeriods.Add(new BlackoutPeriod
        {
            Id = Guid.NewGuid(),
            TenantId = acme.Id,
            ResourceId = conferenceRoomA.Id,
            StartUtc = now.AddDays(7),
            EndUtc = now.AddDays(7).AddHours(4),
            Reason = "Projector maintenance",
        });

        // Two-year weekly recurring booking (WP-1 acceptance: the model must represent this without a redesign).
        var recurringSeries = new RecurringSeries
        {
            Id = Guid.NewGuid(),
            TenantId = acme.Id,
            ResourceId = hotDesk1.Id,
            UserId = acmeMember.Id,
            StartUtc = new DateTimeOffset(now.UtcDateTime.Date.AddDays(1).AddHours(9), TimeSpan.Zero),
            EndUtc = new DateTimeOffset(now.UtcDateTime.Date.AddDays(1).AddHours(9).AddYears(2), TimeSpan.Zero),
            TimeZoneId = acme.DefaultTimeZoneId,
            Frequency = RecurrenceFrequency.Weekly,
            Interval = 1,
        };
        dbContext.RecurringSeries.Add(recurringSeries);

        var recurringOccurrences = Enumerable.Range(0, 3).Select(week => new Booking
        {
            Id = Guid.NewGuid(),
            TenantId = acme.Id,
            ResourceId = hotDesk1.Id,
            UserId = acmeMember.Id,
            SeriesId = recurringSeries.Id,
            StartUtc = recurringSeries.StartUtc.AddDays(7 * week),
            EndUtc = recurringSeries.StartUtc.AddDays(7 * week).AddHours(8),
            Quantity = 1,
            Status = BookingStatus.Confirmed,
            CreatedAtUtc = now,
        });
        dbContext.Bookings.AddRange(recurringOccurrences);

        // One-off booking pending approval.
        var pendingBooking = new Booking
        {
            Id = Guid.NewGuid(),
            TenantId = acme.Id,
            ResourceId = conferenceRoomA.Id,
            UserId = acmeMember.Id,
            StartUtc = now.Date.AddDays(2).AddHours(10),
            EndUtc = now.Date.AddDays(2).AddHours(11),
            Quantity = 1,
            Status = BookingStatus.Pending,
            CreatedAtUtc = now,
        };
        dbContext.Bookings.Add(pendingBooking);
        dbContext.ApprovalRequests.Add(new ApprovalRequest
        {
            Id = Guid.NewGuid(),
            TenantId = acme.Id,
            BookingId = pendingBooking.Id,
            ApproverId = acmeApprover.Id,
            Status = ApprovalStatus.Pending,
            RequestedAtUtc = now,
            ExpiresAtUtc = now.AddHours(acme.ApprovalExpiryHours),
        });

        // One-off confirmed booking in the other tenant, proving isolation has real data to isolate.
        dbContext.Bookings.Add(new Booking
        {
            Id = Guid.NewGuid(),
            TenantId = globex.Id,
            ResourceId = warRoom.Id,
            UserId = globexMember.Id,
            StartUtc = now.Date.AddDays(3).AddHours(14),
            EndUtc = now.Date.AddDays(3).AddHours(15),
            Quantity = 1,
            Status = BookingStatus.Confirmed,
            CreatedAtUtc = now,
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
