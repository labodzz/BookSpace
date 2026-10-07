using BookSpace.Application.Security;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace BookSpace.Infrastructure.Tests.Persistence;

// Every other Infrastructure test builds its schema via Database.EnsureCreatedAsync(), which generates
// DDL straight from the current EF model snapshot and never executes a single Migrations/*.cs Up()
// method. That means the entire class of bug where a migration's own Up() is internally inconsistent
// (e.g. new-column defaults that violate a CHECK constraint added later in the same Up(), against a
// table that already has rows) has no safety net anywhere else in this suite. This file is the one
// place that actually applies real migrations via IMigrator, specifically to close that gap - found
// during a coverage audit of the RedesignRecurringSeriesAndNullableApproverId migration, whose original
// Up() would have thrown a CHECK-constraint violation against any pre-existing RecurringSeries row
// (Quantity defaulted to 0 vs. "> 0"; EndDate/OccurrenceCount both defaulted to null vs. "exactly one
// non-null"; StartTime/EndTime both defaulted to 00:00:00 vs. "EndTime > StartTime") before the defaults
// were fixed.
public sealed class MigrationApplicationTests : IAsyncLifetime
{
    private const string PreRedesignMigration = "20260904094057_AddResourceAndBlackoutPeriodRowVersion";

    private readonly string _connectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database=BookSpaceMigrationTest_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var dbContext = CreateDbContext();
        await dbContext.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task ApplyingTheRedesignRecurringSeriesMigration_AgainstATableWithAnExistingRow_SucceedsAndPreservesTheRow()
    {
        var tenantId = Guid.NewGuid();
        var resourceTypeId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();

        await using (var dbContext = CreateDbContext())
        {
            var migrator = dbContext.GetService<IMigrator>();

            // Migrate only up to the migration immediately BEFORE the one under test - at this point
            // RecurringSeries still has the old StartUtc/EndUtc shape, and (since AddUserStatusAndInvitations
            // is chronologically LATER than PreRedesignMigration) Users doesn't have its Status column yet
            // either. Tenants/ResourceTypes/Resources are untouched by anything after this checkpoint, so
            // the ordinary DbContext API is still safe for seeding those; Users needs the same raw-SQL
            // treatment as the RecurringSeries row below, for the identical reason.
            await migrator.MigrateAsync(PreRedesignMigration);

            var now = DateTimeOffset.UtcNow;
            // Raw SQL, not dbContext.Tenants.Add(...): the live EF model now maps CheckInLeadMinutes (added
            // by a migration chronologically AFTER this checkpoint - see AddTenantCheckInLeadMinutesAndBookingCheckInIndex),
            // so an ordinary tracked insert would try to write a column that doesn't exist in the physical
            // table yet at this frozen migration point - same reasoning as the Users/RecurringSeries raw-SQL
            // inserts below, for the same kind of column.
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Tenants (Id, Name, DefaultTimeZoneId, Status, ReminderLeadMinutes, NoShowGraceMinutes, ApprovalExpiryHours, CreatedAtUtc)
                VALUES ({tenantId}, {"Migration Test Tenant"}, {"UTC"}, {"Active"}, {30}, {15}, {48}, {now})
                """);
            dbContext.ResourceTypes.Add(new ResourceType { Id = resourceTypeId, TenantId = tenantId, Name = "Meeting Room" });
            dbContext.Resources.Add(new Resource
            {
                Id = resourceId, TenantId = tenantId, ResourceTypeId = resourceTypeId, Name = "Migration Test Room",
                Capacity = 2, RequiresApproval = false, Status = ResourceStatus.Active, TimeZoneId = "UTC",
            });
            await dbContext.SaveChangesAsync();

            // A real pre-existing User row in the OLD (no Status column) shape, inserted via raw SQL
            // since the current EF model maps a Status column that doesn't exist yet at this checkpoint.
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Users (Id, TenantId, FirstName, LastName, Email, PasswordHash, CreatedAtUtc)
                VALUES ({userId}, {tenantId}, {"Migration"}, {"Test"}, {"migration@constraint.test"}, {"x"}, {now})
                """);

            // A real pre-existing RecurringSeries row in the OLD (StartUtc/EndUtc) shape, inserted via
            // raw SQL since the current EF model no longer maps those columns at all.
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO RecurringSeries (Id, TenantId, ResourceId, UserId, StartUtc, EndUtc, TimeZoneId, Frequency, Interval)
                VALUES ({seriesId}, {tenantId}, {resourceId}, {userId}, {now.AddDays(1)}, {now.AddDays(1).AddHours(1)}, 'UTC', 'Daily', 1)
                """);

            // The migration under test - this must not throw, even against the row inserted above.
            await migrator.MigrateAsync();
        }

        await using var verifyContext = CreateDbContext(tenantId);
        var series = await verifyContext.RecurringSeries.SingleAsync(s => s.Id == seriesId);
        Assert.Equal(1, series.Quantity);
        Assert.Equal(1, series.OccurrenceCount);
        Assert.Null(series.EndDate);
        Assert.True(series.EndTime > series.StartTime);
    }

    // AddUserStatusAndInvitations adds a NOT NULL Status column to a table that can already have rows,
    // backed by a DB DEFAULT constraint (not an application-side backfill) - this proves that migration
    // actually applies cleanly against a pre-existing Users row and that the DEFAULT constraint backfills
    // it to Active, matching User.cs's own documented reasoning for why Active had to be the enum's zero
    // member (see that file's comment on the EF "unset vs. explicitly set" sentinel).
    private const string PreUserStatusMigration = "20260915120000_BackfillResourceTypesTenantId";

    [Fact]
    public async Task ApplyingTheAddUserStatusAndInvitationsMigration_BackfillsAnExistingUserRowToActive()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using (var dbContext = CreateDbContext())
        {
            var migrator = dbContext.GetService<IMigrator>();
            await migrator.MigrateAsync(PreUserStatusMigration);

            var now = DateTimeOffset.UtcNow;
            // Raw SQL, not dbContext.Tenants.Add(...) - same reasoning as the other migration test above:
            // the live EF model maps CheckInLeadMinutes, which doesn't exist in the physical table yet at
            // this checkpoint.
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Tenants (Id, Name, DefaultTimeZoneId, Status, ReminderLeadMinutes, NoShowGraceMinutes, ApprovalExpiryHours, CreatedAtUtc)
                VALUES ({tenantId}, {"Backfill Test Tenant"}, {"UTC"}, {"Active"}, {30}, {15}, {48}, {now})
                """);

            // Users has no Status column yet at this checkpoint - raw SQL, same reasoning as the
            // RecurringSeries/Users seeding above.
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Users (Id, TenantId, FirstName, LastName, Email, PasswordHash, CreatedAtUtc)
                VALUES ({userId}, {tenantId}, {"Pre"}, {"Existing"}, {"pre-existing@backfill.test"}, {"x"}, {now})
                """);

            await migrator.MigrateAsync();
        }

        await using var verifyContext = CreateDbContext(tenantId);
        var user = await verifyContext.Users.SingleAsync(u => u.Id == userId);
        Assert.Equal(UserStatus.Active, user.Status);
    }

    // ReleaseTenantNameAndUserEmailOnSoftDelete's own backfill (see that migration's doc comment) must
    // catch up tenants that were ALREADY soft-deleted before this feature existed: their users are still
    // sitting at whatever live status they had at deletion time, which would otherwise keep blocking their
    // name/email from ever being reused once the new filtered indexes start enforcing
    // "[Status] <> 'Withdrawn'"/"[Status] <> 'Deleted'". No column shapes change between this checkpoint
    // and the migration under test, so seeding via the ordinary DbContext API (not raw SQL) is safe here,
    // unlike the two migration tests above.
    private const string PreReleaseNameAndEmailMigration = "20261006184600_MakeUserTenantIdNullable";

    [Fact]
    public async Task ApplyingTheReleaseTenantNameAndUserEmailMigration_BackfillsAnAlreadyDeletedTenantsUsersAndRevokesTheirTokens()
    {
        var deletedTenantId = Guid.NewGuid();
        var activeUserId = Guid.NewGuid();
        var invitedUserId = Guid.NewGuid();
        var deletedTenantName = $"Pre-Existing Deleted Tenant {Guid.NewGuid():N}";
        var activeUserEmail = $"pre-existing-active-{Guid.NewGuid():N}@migration.test";
        var invitedUserEmail = $"pre-existing-invited-{Guid.NewGuid():N}@migration.test";
        Guid invitationId, subscriptionId, refreshTokenId;

        await using (var dbContext = CreateDbContext())
        {
            var migrator = dbContext.GetService<IMigrator>();
            await migrator.MigrateAsync(PreReleaseNameAndEmailMigration);

            var now = DateTimeOffset.UtcNow;
            dbContext.Tenants.Add(new Tenant
            {
                Id = deletedTenantId, Name = deletedTenantName, DefaultTimeZoneId = "UTC", Status = TenantStatus.Deleted, CreatedAtUtc = now,
            });
            dbContext.Users.Add(new User
            {
                Id = activeUserId, TenantId = deletedTenantId, FirstName = "Was", LastName = "Active",
                Email = activeUserEmail, PasswordHash = "x", Status = UserStatus.Active, CreatedAtUtc = now,
            });
            dbContext.Users.Add(new User
            {
                Id = invitedUserId, TenantId = deletedTenantId, FirstName = "Was", LastName = "Invited",
                Email = invitedUserEmail, PasswordHash = "", Status = UserStatus.Invited, CreatedAtUtc = now,
            });
            invitationId = Guid.NewGuid();
            dbContext.Invitations.Add(new Invitation
            {
                Id = invitationId, TenantId = deletedTenantId, UserId = invitedUserId, TokenHash = $"hash-{Guid.NewGuid()}",
                CreatedByUserId = activeUserId, CreatedAtUtc = now, ExpiresAtUtc = now.AddHours(72),
            });
            subscriptionId = Guid.NewGuid();
            dbContext.CalendarSubscriptions.Add(new CalendarSubscription
            {
                Id = subscriptionId, TenantId = deletedTenantId, UserId = activeUserId, TokenHash = $"hash-{Guid.NewGuid()}", CreatedAtUtc = now,
            });
            refreshTokenId = Guid.NewGuid();
            dbContext.RefreshTokens.Add(new RefreshToken
            {
                Id = refreshTokenId, UserId = activeUserId, FamilyId = Guid.NewGuid(), TokenHash = $"hash-{Guid.NewGuid()}",
                CreatedAtUtc = now, ExpiresAtUtc = now.AddDays(30),
            });
            await dbContext.SaveChangesAsync();

            // The migration under test.
            await migrator.MigrateAsync();
        }

        await using (var verifyContext = CreateDbContext())
        {
            Assert.Equal(UserStatus.Withdrawn, (await verifyContext.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == activeUserId)).Status);
            Assert.Equal(UserStatus.Withdrawn, (await verifyContext.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == invitedUserId)).Status);
            Assert.NotNull((await verifyContext.Invitations.IgnoreQueryFilters().SingleAsync(i => i.Id == invitationId)).RevokedAtUtc);
            Assert.NotNull((await verifyContext.CalendarSubscriptions.IgnoreQueryFilters().SingleAsync(s => s.Id == subscriptionId)).RevokedAtUtc);
            Assert.NotNull((await verifyContext.RefreshTokens.SingleAsync(t => t.Id == refreshTokenId)).RevokedAtUtc);
        }

        // The whole point: the backfilled Withdrawn status/Deleted tenant immediately free up the name and
        // both emails for brand-new rows - proving the backfill ran BEFORE the new filtered indexes started
        // enforcing anything, not merely that the columns above were updated in isolation.
        await using (var reuseContext = CreateDbContext())
        {
            var newTenantId = Guid.NewGuid();
            reuseContext.Tenants.Add(new Tenant
            {
                Id = newTenantId, Name = deletedTenantName, DefaultTimeZoneId = "UTC", Status = TenantStatus.Active, CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            reuseContext.Users.Add(new User
            {
                Id = Guid.NewGuid(), TenantId = newTenantId, FirstName = "New", LastName = "Owner",
                Email = activeUserEmail, PasswordHash = "x", Status = UserStatus.Active, CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            reuseContext.Users.Add(new User
            {
                Id = Guid.NewGuid(), TenantId = newTenantId, FirstName = "New", LastName = "Member",
                Email = invitedUserEmail, PasswordHash = "x", Status = UserStatus.Active, CreatedAtUtc = DateTimeOffset.UtcNow,
            });

            await reuseContext.SaveChangesAsync(); // does not throw
        }
    }

    private BookSpaceDbContext CreateDbContext(Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>().UseSqlServer(_connectionString).Options;
        return new BookSpaceDbContext(options, new FixedCurrentUserContext(tenantId));
    }

    private sealed class FixedCurrentUserContext(Guid? tenantId) : ICurrentUserContext
    {
        public Guid? UserId => null;
        public Guid? TenantId => tenantId;
        public IReadOnlyCollection<string> Roles => [];
    }
}
