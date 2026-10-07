using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

internal static class BookSpaceModelConfiguration
{
    internal static void ConfigureBookSpaceModel(this ModelBuilder builder, bool useRowVersionColumns)
    {
        builder.Entity<Tenant>(entity =>
        {
            entity.ToTable("Tenants", table =>
            {
                table.HasCheckConstraint("CK_Tenants_ReminderLeadMinutes", "[ReminderLeadMinutes] >= 0");
                table.HasCheckConstraint("CK_Tenants_NoShowGraceMinutes", "[NoShowGraceMinutes] >= 0");
                table.HasCheckConstraint("CK_Tenants_ApprovalExpiryHours", "[ApprovalExpiryHours] > 0");
                table.HasCheckConstraint("CK_Tenants_CheckInLeadMinutes", "[CheckInLeadMinutes] >= 0");
            });
            // Explicit SQL-level default (matching Tenant.CheckInLeadMinutes' C# default): this column is
            // added to an ALREADY-EXISTING table, so without this every pre-existing tenant would silently
            // backfill to 0 (no check-in lead time at all) instead of the documented default of 15 minutes.
            entity.Property(tenant => tenant.CheckInLeadMinutes).HasDefaultValue(15);
            entity.HasKey(tenant => tenant.Id);
            entity.Property(tenant => tenant.Name).HasMaxLength(200).IsRequired();
            entity.Property(tenant => tenant.DefaultTimeZoneId).HasMaxLength(100).IsRequired();
            entity.Property(tenant => tenant.Status).HasConversion<string>().HasMaxLength(20);
            // Filtered to non-Deleted tenants only - a soft-deleted tenant's name is released for reuse by
            // a brand-new tenant (see DeleteTenantCommandHandler and docs/tenant-onboarding.md, "Tenant
            // lifecycle"). Active and Suspended both still reserve their name - only Deleted is excluded.
            entity.HasIndex(tenant => tenant.Name).IsUnique().HasFilter("[Status] <> 'Deleted'");
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
            entity.Property(user => user.PasswordHash).HasMaxLength(200).IsRequired();
            // HasDefaultValue, not just a column default in the migration alone - EF must know about
            // it too, since User.Status's CLR default (UserStatus.Active, the enum's zero member) is
            // exactly what tells EF "this property was left at its default, apply the DB DEFAULT
            // constraint" for every existing object-initializer call site that never sets Status
            // explicitly. See User.cs's own comment for why Active had to be the zero member.
            entity.Property(user => user.Status).HasConversion<string>().HasMaxLength(20).HasDefaultValue(UserStatus.Active);
            // Global, not per-tenant: login looks a user up by email alone, before any tenant is
            // known, so two tenants sharing an email would make that lookup ambiguous. Stays global
            // (not scoped around the null TenantId case) even for the global SysAdmin: one platform-
            // wide identity space, same as everyone else.
            //
            // Filtered to non-Withdrawn users only - a user whose tenant was soft-deleted (Status ->
            // Withdrawn, see DeleteTenantCommandHandler/UserStatus) releases their email for a brand-new
            // account to use, in this tenant or a new one. A plain "exclude Inactive" filter would be wrong
            // here: Inactive is an administrator's own reversible deactivation of a still-live tenant
            // member and must keep reserving that email (see UserStatus's own doc comment for why these are
            // deliberately different statuses) - this can never be expressed as a filter on the related
            // Tenants row (a SQL Server filtered index predicate must be a constant expression over this
            // table's own columns), which is exactly why Withdrawn exists as its own User-level status.
            entity.HasIndex(user => user.Email).IsUnique().HasFilter("[Status] <> 'Withdrawn'");
            // IsRequired(false) explicit, not just inferred from the nullable CLR property - TenantId
            // nullability is the load-bearing part of the whole SysAdmin-is-global invariant, so this
            // FK's optionality is spelled out rather than left to EF's default inference.
            entity.HasOne<Tenant>().WithMany().HasForeignKey(user => user.TenantId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Invitation>(entity =>
        {
            entity.ToTable("Invitations");
            entity.HasKey(invitation => invitation.Id);
            entity.Property(invitation => invitation.TokenHash).HasMaxLength(200).IsRequired();
            entity.HasIndex(invitation => invitation.TokenHash).IsUnique();
            entity.HasIndex(invitation => invitation.TenantId);
            entity.HasIndex(invitation => invitation.CreatedByUserId);
            // At most one ACTIVE (not yet accepted, not yet revoked) invitation per user - a reissue
            // must revoke the previous one first (see InviteUserCommandHandler), never leave two
            // active rows racing. Accepted/revoked rows are excluded from the filter, so history
            // accumulates freely across repeated invite/reissue cycles for the same user.
            entity.HasIndex(invitation => invitation.UserId)
                .IsUnique()
                .HasFilter("[AcceptedAtUtc] IS NULL AND [RevokedAtUtc] IS NULL");
            entity.HasOne<Tenant>().WithMany().HasForeignKey(invitation => invitation.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(invitation => invitation.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<User>().WithMany().HasForeignKey(invitation => invitation.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            if (useRowVersionColumns)
            {
                entity.Property(invitation => invitation.RowVersion).IsRowVersion();
            }
        });

        builder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("RefreshTokens");
            entity.HasKey(token => token.Id);
            entity.Property(token => token.TokenHash).HasMaxLength(200).IsRequired();
            entity.HasIndex(token => token.TokenHash).IsUnique();
            entity.HasIndex(token => token.FamilyId);
            entity.HasOne<User>().WithMany().HasForeignKey(token => token.UserId).OnDelete(DeleteBehavior.Cascade);
            if (useRowVersionColumns)
            {
                entity.Property(token => token.RowVersion).IsRowVersion();
            }
        });

        builder.Entity<CalendarSubscription>(entity =>
        {
            entity.ToTable("CalendarSubscriptions");
            entity.HasKey(subscription => subscription.Id);
            entity.Property(subscription => subscription.TokenHash).HasMaxLength(200).IsRequired();
            entity.HasIndex(subscription => subscription.TokenHash).IsUnique();
            entity.HasIndex(subscription => subscription.TenantId);
            // At most one ACTIVE (not yet revoked) subscription per user - same filtered-unique-index
            // shape as Invitation.UserId. Regenerating revokes the previous active row and inserts a new
            // one (never mutates TokenHash in place), so the history of every subscription this user has
            // ever had is preserved rather than overwritten.
            entity.HasIndex(subscription => subscription.UserId)
                .IsUnique()
                .HasFilter("[RevokedAtUtc] IS NULL");
            entity.HasOne<Tenant>().WithMany().HasForeignKey(subscription => subscription.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(subscription => subscription.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ResourceType>(entity =>
        {
            entity.ToTable("ResourceTypes");
            entity.HasKey(type => type.Id);
            entity.Property(type => type.Name).HasMaxLength(100).IsRequired();
            entity.HasIndex(type => new { type.TenantId, type.Name }).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(type => type.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Resource>(entity =>
        {
            entity.ToTable("Resources", table => table.HasCheckConstraint("CK_Resources_Capacity", "[Capacity] > 0"));
            entity.HasKey(resource => resource.Id);
            entity.Property(resource => resource.Name).HasMaxLength(200).IsRequired();
            entity.Property(resource => resource.Description).HasMaxLength(2000);
            entity.Property(resource => resource.TimeZoneId).HasMaxLength(100).IsRequired();
            entity.Property(resource => resource.Status).HasConversion<string>().HasMaxLength(20);
            // Filtered to Active/Inactive/Maintenance only - an Archived resource is effectively
            // deleted from the caller's point of view, so its name is released for reuse rather than
            // reserved forever.
            entity.HasIndex(resource => new { resource.TenantId, resource.Name })
                .IsUnique()
                .HasFilter("[Status] <> 'Archived'");
            entity.HasOne<Tenant>().WithMany().HasForeignKey(resource => resource.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ResourceType>().WithMany().HasForeignKey(resource => resource.ResourceTypeId).OnDelete(DeleteBehavior.Restrict);
            if (useRowVersionColumns)
            {
                entity.Property(resource => resource.RowVersion).IsRowVersion();
            }
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
            if (useRowVersionColumns)
            {
                entity.Property(period => period.RowVersion).IsRowVersion();
            }
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
                table.HasCheckConstraint("CK_RecurringSeries_TimeRange", "[EndTime] > [StartTime]");
                table.HasCheckConstraint("CK_RecurringSeries_Quantity", "[Quantity] > 0");
                table.HasCheckConstraint("CK_RecurringSeries_OccurrenceCount", "[OccurrenceCount] IS NULL OR [OccurrenceCount] > 0");
                // Exactly one end condition - never both, never neither.
                table.HasCheckConstraint(
                    "CK_RecurringSeries_EndCondition",
                    "([EndDate] IS NOT NULL AND [OccurrenceCount] IS NULL) OR ([EndDate] IS NULL AND [OccurrenceCount] IS NOT NULL)");
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
            // Supports BookingReminderReader's system-wide due query (WHERE Status = 'Confirmed' AND
            // ReminderQueuedAtUtc IS NULL AND StartUtc in a future range ORDER BY StartUtc) - the index
            // above starts with TenantId, so it cannot help a query that must scan every tenant in one
            // call; this one starts directly with StartUtc and is filtered to exactly the reader's own
            // predicate, the same filtered-covering-index pattern as NotificationOutboxItems' own
            // due-batch index (see that entity's own index comment above), for exactly the same reason:
            // empirically confirmed (docs/background-jobs.md, "Reminder job - index") that INCLUDE-ing
            // every column the reader's projection needs turns this into an Index Seek instead of a
            // Clustered Index Scan. The filter must name BOTH conditions the query itself checks - SQL
            // Server can only use a filtered index when the query's WHERE clause provably implies the
            // index's filter predicate, so a filter of "Status = 'Confirmed'" alone would no longer match
            // once the query also checks ReminderQueuedAtUtc IS NULL.
            entity.HasIndex(booking => booking.StartUtc)
                .HasFilter("[Status] = 'Confirmed' AND [ReminderQueuedAtUtc] IS NULL")
                .IncludeProperties(
                    booking => new { booking.TenantId, booking.UserId, booking.ResourceId, booking.EndUtc, booking.Quantity, booking.CreatedAtUtc });
            // Prepared for the future no-show job (not yet implemented - see docs/background-jobs.md,
            // "What the earlier WP-8 branches did not implement"), which will need "Confirmed bookings
            // nobody checked in, ordered by StartUtc" as its own due-candidate shape. Deliberately does NOT
            // bake in the grace-period arithmetic itself (StartUtc + Tenant.NoShowGraceMinutes <= now):
            // NoShowGraceMinutes is per-tenant, and a SQL Server filtered-index predicate must be a constant
            // expression, so it can never reference another table's column. This index still narrows that
            // future query enormously (Status='Confirmed' AND CheckedInAtUtc IS NULL excludes every booking
            // that already has an outcome), with StartUtc as the key so the eventual per-tenant cutoff check
            // runs against a small, already-ordered candidate set instead of a full table scan - verified by
            // query plan (docs/bookings-and-concurrency.md, "Booking check-in - index for the future no-show
            // job") against a query joining Tenants for the per-tenant grace comparison.
            // The name MUST be passed directly to HasIndex (not attached afterward via .HasDatabaseName()) -
            // EF Core's fluent API keys an index builder by its property list, so a later .HasDatabaseName()
            // call on a second HasIndex(booking => booking.StartUtc) just renames/overwrites the SAME index
            // metadata object as the reminder index above (both are keyed on StartUtc alone), silently
            // collapsing two intended indexes into one and making a migration DROP the reminder index
            // entirely instead of adding a second, independent one - confirmed by inspecting the generated
            // migration and model snapshot before this fix.
            entity.HasIndex(booking => booking.StartUtc, "IX_Bookings_StartUtc_NoShowCandidate")
                .HasFilter("[Status] = 'Confirmed' AND [CheckedInAtUtc] IS NULL")
                .IncludeProperties(
                    booking => new { booking.TenantId, booking.UserId, booking.ResourceId, booking.EndUtc, booking.Quantity, booking.CreatedAtUtc });
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
            // Supports StaleApprovalReader's system-wide due query (WHERE Status = 'Pending' AND
            // ExpiresAtUtc <= now ORDER BY RequestedAtUtc, Id) - the same filtered-covering-index pattern
            // NotificationOutboxItems/Bookings already use for their own due queries, for the same reason:
            // a filtered index key on the due-timestamp column, INCLUDE-ing exactly what the reader's
            // projection (plus the join back to Bookings) needs, filtered to the one status that is ever
            // "due" so the index shrinks as requests leave Pending instead of growing unbounded forever.
            entity.HasIndex(request => request.ExpiresAtUtc)
                .HasFilter("[Status] = 'Pending'")
                .IncludeProperties(request => new { request.TenantId, request.BookingId, request.RequestedAtUtc });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(request => request.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Booking>().WithMany().HasForeignKey(request => request.BookingId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<User>().WithMany().HasForeignKey(request => request.ApproverId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<JobLease>(entity =>
        {
            entity.ToTable("JobLeases");
            // JobName as the primary key (not merely a unique index) is the physical guarantee that two
            // active lease rows for the same job can never exist - see docs/background-jobs.md.
            entity.HasKey(lease => lease.JobName);
            entity.Property(lease => lease.JobName).HasMaxLength(200);
            entity.Property(lease => lease.OwnerId).HasMaxLength(200).IsRequired();
        });

        builder.Entity<NotificationOutboxItem>(entity =>
        {
            entity.ToTable("NotificationOutboxItems");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.NotificationType).HasMaxLength(100).IsRequired();
            entity.Property(item => item.IdempotencyKey).HasMaxLength(NotificationOutboxItem.MaxIdempotencyKeyLength).IsRequired();
            entity.Property(item => item.PayloadJson).IsRequired();
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(item => item.LastError).HasMaxLength(NotificationOutboxItem.MaxLastErrorLength);
            // Tenant-scoped, not globally unique - see docs/background-jobs.md for why: this schema's
            // convention scopes uniqueness to the tenant-owning column for everything except a
            // pre-authentication global lookup (User.Email), and a notification idempotency key has no
            // comparable reason to be compared across tenants.
            entity.HasIndex(item => new { item.TenantId, item.IdempotencyKey }).IsUnique();
            // Supports the due-batch query (WHERE Status = 'Pending' AND AvailableAtUtc <= now ORDER BY
            // AvailableAtUtc) - filtered to Pending only, like Resource's and Invitation's filtered
            // indexes elsewhere in this file, so the index shrinks as items are marked Sent instead of
            // growing unbounded forever. INCLUDE makes it a COVERING index for NotificationOutboxReader's
            // exact projection - empirically confirmed (see docs/background-jobs.md, "Due-batch query and
            // its index") that WITHOUT these INCLUDE columns, SQL Server's optimizer chose a full
            // Clustered Index Scan over seeking this index, even at realistic due/total selectivity,
            // because satisfying the SELECT would otherwise need a key lookup per matching row. With them,
            // it chooses an Index Seek instead.
            entity.HasIndex(item => item.AvailableAtUtc)
                .HasFilter("[Status] = 'Pending'")
                .IncludeProperties(
                    item => new { item.TenantId, item.NotificationType, item.RecipientUserId, item.PayloadJson, item.CreatedAtUtc, item.AttemptCount });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(item => item.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(item => item.RecipientUserId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
