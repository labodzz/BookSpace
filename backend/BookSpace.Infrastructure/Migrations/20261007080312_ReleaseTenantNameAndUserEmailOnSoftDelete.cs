using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Migrations
{
    /// <inheritdoc />
    // Tenant.Name's unique index has always been global/unfiltered, so no pre-existing row can possibly
    // violate the new, strictly MORE permissive filtered version below - no backfill is needed for it.
    // Users.Email is different: the backfill below must run BEFORE the new filtered index is created,
    // not after, so that a tenant soft-deleted before this migration existed (whose users are still sitting
    // at their pre-deletion Active/Invited/Inactive status) has its users' emails already released by the
    // time the filtered index starts enforcing "[Status] <> 'Withdrawn'" - see DeleteTenantCommandHandler
    // and UserStatus for what a NEWLY soft-deleted tenant does going forward; this is the one-time catch-up
    // for data that predates that handler's cascade.
    //
    // Also revokes every still-active Invitation/CalendarSubscription/RefreshToken belonging to one of
    // those now-withdrawn users - the identical cascade DeleteTenantCommandHandler now performs for every
    // FUTURE deletion (see its own doc comment), applied once, retroactively, to tenants that were deleted
    // before this task existed and therefore never had that cascade run for them at all.
    public partial class ReleaseTenantNameAndUserEmailOnSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE Users
                SET Status = 'Withdrawn'
                WHERE Status <> 'Withdrawn'
                  AND TenantId IN (SELECT Id FROM Tenants WHERE Status = 'Deleted');

                UPDATE Invitations
                SET RevokedAtUtc = SYSDATETIMEOFFSET()
                WHERE RevokedAtUtc IS NULL
                  AND AcceptedAtUtc IS NULL
                  AND TenantId IN (SELECT Id FROM Tenants WHERE Status = 'Deleted');

                UPDATE CalendarSubscriptions
                SET RevokedAtUtc = SYSDATETIMEOFFSET()
                WHERE RevokedAtUtc IS NULL
                  AND TenantId IN (SELECT Id FROM Tenants WHERE Status = 'Deleted');

                UPDATE RefreshTokens
                SET RevokedAtUtc = SYSDATETIMEOFFSET()
                WHERE RevokedAtUtc IS NULL
                  AND UserId IN (
                      SELECT Id FROM Users WHERE TenantId IN (SELECT Id FROM Tenants WHERE Status = 'Deleted')
                  );
                """);

            migrationBuilder.DropIndex(
                name: "IX_Users_Email",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Tenants_Name",
                table: "Tenants");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true,
                filter: "[Status] <> 'Withdrawn'");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Name",
                table: "Tenants",
                column: "Name",
                unique: true,
                filter: "[Status] <> 'Deleted'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_Email",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Tenants_Name",
                table: "Tenants");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Name",
                table: "Tenants",
                column: "Name",
                unique: true);

            // The Status/RevokedAtUtc backfill above is NOT reversible - there is no way to recover which
            // rows were already Withdrawn/revoked for some other, legitimate reason versus backfilled by
            // this migration. A rolled-back environment is left with those already-deleted tenants' users
            // still Withdrawn (so still unable to log in - correct, their tenant is still Deleted) but
            // without the new filtered indexes enforcing anything beyond the old global uniqueness, which
            // only matters in combination with a DeleteTenantCommandHandler build that no longer exists
            // once this migration is rolled back.
        }
    }
}
