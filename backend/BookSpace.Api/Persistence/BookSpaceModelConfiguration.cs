using BookSpace.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Api.Persistence;

internal static class BookSpaceModelConfiguration
{
    internal static void ConfigureBookSpaceModel(this ModelBuilder builder)
    {
        builder.Entity<Tenant>(entity =>
        {
            entity.ToTable("Tenants", table =>
            {
                table.HasCheckConstraint("CK_Tenants_ReminderLeadMinutes", "[ReminderLeadMinutes] >= 0");
                table.HasCheckConstraint("CK_Tenants_NoShowGraceMinutes", "[NoShowGraceMinutes] >= 0");
                table.HasCheckConstraint("CK_Tenants_ApprovalExpiryHours", "[ApprovalExpiryHours] > 0");
            });
            entity.HasKey(tenant => tenant.Id);
            entity.Property(tenant => tenant.Name).HasMaxLength(200).IsRequired();
            entity.Property(tenant => tenant.DefaultTimeZoneId).HasMaxLength(100).IsRequired();
            entity.Property(tenant => tenant.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(tenant => tenant.Name).IsUnique();
        });

        builder.Entity<Role>(entity =>
        {
            entity.ToTable("Roles");
            entity.HasKey(role => role.Id);
            entity.Property(role => role.Name).HasMaxLength(100).IsRequired();
            entity.Property(role => role.Description).HasMaxLength(500);
            entity.HasIndex(role => role.Name).IsUnique();
        });

        builder.Entity<UserRole>(entity =>
        {
            entity.ToTable("UserRoles");
            entity.HasKey(userRole => userRole.Id);
            entity.HasIndex(userRole => new { userRole.UserId, userRole.RoleId }).IsUnique();
            entity.HasOne<User>().WithMany().HasForeignKey(userRole => userRole.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Role>().WithMany().HasForeignKey(userRole => userRole.RoleId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<User>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.FirstName).HasMaxLength(100).IsRequired();
            entity.Property(user => user.LastName).HasMaxLength(100).IsRequired();
            entity.Property(user => user.Email).HasMaxLength(320).IsRequired();
            entity.HasIndex(user => new { user.TenantId, user.Email }).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(user => user.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ResourceType>(entity =>
        {
            entity.ToTable("ResourceTypes");
            entity.HasKey(type => type.Id);
            entity.Property(type => type.Name).HasMaxLength(100).IsRequired();
            entity.HasIndex(type => type.Name).IsUnique();
        });

        builder.Entity<Resource>(entity =>
        {
            entity.ToTable("Resources", table => table.HasCheckConstraint("CK_Resources_Capacity", "[Capacity] > 0"));
            entity.HasKey(resource => resource.Id);
            entity.Property(resource => resource.Name).HasMaxLength(200).IsRequired();
            entity.Property(resource => resource.Description).HasMaxLength(2000);
            entity.Property(resource => resource.TimeZoneId).HasMaxLength(100).IsRequired();
            entity.Property(resource => resource.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(resource => new { resource.TenantId, resource.Name }).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(resource => resource.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ResourceType>().WithMany().HasForeignKey(resource => resource.ResourceTypeId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<AvailabilityRule>(entity =>
        {
            entity.ToTable("AvailabilityRules", table =>
                table.HasCheckConstraint("CK_AvailabilityRules_TimeRange", "[EndTime] > [StartTime]"));
            entity.HasKey(rule => rule.Id);
            entity.HasIndex(rule => new { rule.ResourceId, rule.DayOfWeek, rule.StartTime, rule.EndTime }).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(rule => rule.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Resource>().WithMany().HasForeignKey(rule => rule.ResourceId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<BlackoutPeriod>(entity =>
        {
            entity.ToTable("BlackoutPeriods", table =>
                table.HasCheckConstraint("CK_BlackoutPeriods_TimeRange", "[EndUtc] > [StartUtc]"));
            entity.HasKey(period => period.Id);
            entity.Property(period => period.Reason).HasMaxLength(1000).IsRequired();
            entity.HasIndex(period => new { period.ResourceId, period.StartUtc, period.EndUtc });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(period => period.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Resource>().WithMany().HasForeignKey(period => period.ResourceId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ResourceApprover>(entity =>
        {
            entity.ToTable("ResourceApprovers");
            entity.HasKey(approver => approver.Id);
            entity.HasIndex(approver => new { approver.ResourceId, approver.UserId }).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(approver => approver.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Resource>().WithMany().HasForeignKey(approver => approver.ResourceId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<User>().WithMany().HasForeignKey(approver => approver.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<RecurringSeries>(entity =>
        {
            entity.ToTable("RecurringSeries", table =>
            {
                table.HasCheckConstraint("CK_RecurringSeries_Interval", "[Interval] > 0");
                table.HasCheckConstraint("CK_RecurringSeries_TimeRange", "[EndUtc] IS NULL OR [EndUtc] > [StartUtc]");
            });
            entity.HasKey(series => series.Id);
            entity.Property(series => series.TimeZoneId).HasMaxLength(100).IsRequired();
            entity.Property(series => series.Frequency).HasConversion<string>().HasMaxLength(20);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(series => series.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Resource>().WithMany().HasForeignKey(series => series.ResourceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(series => series.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Booking>(entity =>
        {
            entity.ToTable("Bookings", table =>
            {
                table.HasCheckConstraint("CK_Bookings_TimeRange", "[EndUtc] > [StartUtc]");
                table.HasCheckConstraint("CK_Bookings_Quantity", "[Quantity] > 0");
            });
            entity.HasKey(booking => booking.Id);
            entity.Property(booking => booking.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(booking => booking.CancellationReason).HasMaxLength(1000);
            // Primary lookup shape for availability/overlap checks: "what's booked on this resource in this window".
            entity.HasIndex(booking => new { booking.TenantId, booking.ResourceId, booking.StartUtc, booking.EndUtc });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(booking => booking.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Resource>().WithMany().HasForeignKey(booking => booking.ResourceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(booking => booking.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(booking => booking.CancelledByUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<RecurringSeries>().WithMany().HasForeignKey(booking => booking.SeriesId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<ApprovalRequest>(entity =>
        {
            entity.ToTable("ApprovalRequests", table =>
                table.HasCheckConstraint("CK_ApprovalRequests_Expiry", "[ExpiresAtUtc] > [RequestedAtUtc]"));
            entity.HasKey(request => request.Id);
            entity.Property(request => request.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(request => request.DecisionNote).HasMaxLength(2000);
            entity.HasIndex(request => request.BookingId).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(request => request.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Booking>().WithMany().HasForeignKey(request => request.BookingId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<User>().WithMany().HasForeignKey(request => request.ApproverId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
