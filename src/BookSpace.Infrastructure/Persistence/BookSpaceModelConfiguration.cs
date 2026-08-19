using BookSpace.Domain.Bookings;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Notifications;
using BookSpace.Domain.Resources;
using BookSpace.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

internal static class BookSpaceModelConfiguration
{
    internal static void ConfigureBookSpaceModel(this ModelBuilder builder)
    {
        builder.Entity<Tenant>(entity =>
        {
            entity.ToTable("Tenants");
            entity.HasKey(tenant => tenant.Id);
            entity.Property(tenant => tenant.Name).HasMaxLength(200).IsRequired();
            entity.HasIndex(tenant => tenant.Name).IsUnique();
        });

        builder.Entity<ApplicationUser>(entity =>
            entity.HasOne<Tenant>().WithMany().HasForeignKey(user => user.TenantId).OnDelete(DeleteBehavior.Restrict));

        builder.Entity<ApplicationRole>(entity => entity.Property(role => role.Description).HasMaxLength(500));

        builder.Entity<ResourceType>(entity =>
        {
            entity.ToTable("ResourceTypes");
            entity.HasKey(type => type.Id);
            entity.Property(type => type.Name).HasMaxLength(100).IsRequired();
            entity.HasIndex(type => new { type.TenantId, type.Name }).IsUnique();
        });

        builder.Entity<Resource>(entity =>
        {
            entity.ToTable("Resources");
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
            entity.ToTable("AvailabilityRules");
            entity.HasKey(rule => rule.Id);
            entity.HasIndex(rule => new { rule.ResourceId, rule.DayOfWeek, rule.StartTime, rule.EndTime }).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(rule => rule.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Resource>().WithMany().HasForeignKey(rule => rule.ResourceId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<BlackoutPeriod>(entity =>
        {
            entity.ToTable("BlackoutPeriods");
            entity.HasKey(period => period.Id);
            entity.Property(period => period.Reason).HasMaxLength(1000).IsRequired();
            entity.HasIndex(period => new { period.ResourceId, period.StartUtc, period.EndUtc });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(period => period.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Resource>().WithMany().HasForeignKey(period => period.ResourceId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ResourceApprover>(entity =>
        {
            entity.ToTable("ResourceApprovers");
            entity.HasKey(approver => new { approver.TenantId, approver.ResourceId, approver.UserId });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(approver => approver.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Resource>().WithMany().HasForeignKey(approver => approver.ResourceId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(approver => approver.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<RecurringSeries>(entity =>
        {
            entity.ToTable("RecurringSeries");
            entity.HasKey(series => series.Id);
            entity.Property(series => series.TimeZoneId).HasMaxLength(100).IsRequired();
            entity.Property(series => series.Frequency).HasConversion<string>().HasMaxLength(20);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(series => series.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Resource>().WithMany().HasForeignKey(series => series.ResourceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(series => series.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Booking>(entity =>
        {
            entity.ToTable("Bookings");
            entity.HasKey(booking => booking.Id);
            entity.Property(booking => booking.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(booking => new { booking.TenantId, booking.ResourceId, booking.StartUtc, booking.EndUtc });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(booking => booking.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Resource>().WithMany().HasForeignKey(booking => booking.ResourceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(booking => booking.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<RecurringSeries>().WithMany().HasForeignKey(booking => booking.SeriesId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<ApprovalRequest>(entity =>
        {
            entity.ToTable("ApprovalRequests");
            entity.HasKey(request => request.Id);
            entity.Property(request => request.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(request => request.DecisionNote).HasMaxLength(2000);
            entity.HasIndex(request => request.BookingId).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(request => request.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Booking>().WithMany().HasForeignKey(request => request.BookingId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(request => request.ApproverId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Notification>(entity =>
        {
            entity.ToTable("Notifications");
            entity.HasKey(notification => notification.Id);
            entity.Property(notification => notification.Type).HasConversion<string>().HasMaxLength(50);
            entity.Property(notification => notification.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(notification => notification.Subject).HasMaxLength(500).IsRequired();
            entity.Property(notification => notification.IdempotencyKey).HasMaxLength(200).IsRequired();
            entity.HasIndex(notification => new { notification.TenantId, notification.IdempotencyKey }).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(notification => notification.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Booking>().WithMany().HasForeignKey(notification => notification.BookingId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(notification => notification.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("RefreshTokens");
            entity.HasKey(token => token.Id);
            entity.Property(token => token.TokenHash).HasMaxLength(500).IsRequired();
            entity.HasIndex(token => token.TokenHash).IsUnique();
            entity.HasIndex(token => new { token.UserId, token.FamilyId });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(token => token.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(token => token.UserId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
