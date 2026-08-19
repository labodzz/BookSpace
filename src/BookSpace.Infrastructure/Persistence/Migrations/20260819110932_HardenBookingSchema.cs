using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenBookingSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_Resources_Capacity",
                table: "Resources",
                sql: "[Capacity] > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RecurringSeries_EndCondition",
                table: "RecurringSeries",
                sql: "([UntilDate] IS NOT NULL OR [OccurrenceCount] IS NOT NULL) AND ([OccurrenceCount] IS NULL OR [OccurrenceCount] > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RecurringSeries_Interval",
                table: "RecurringSeries",
                sql: "[Interval] > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Bookings_TimeRange",
                table: "Bookings",
                sql: "[EndUtc] > [StartUtc]");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BlackoutPeriods_TimeRange",
                table: "BlackoutPeriods",
                sql: "[EndUtc] > [StartUtc]");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AvailabilityRules_TimeRange",
                table: "AvailabilityRules",
                sql: "[EndTime] > [StartTime]");

            migrationBuilder.AddForeignKey(
                name: "FK_ResourceTypes_Tenants_TenantId",
                table: "ResourceTypes",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ResourceTypes_Tenants_TenantId",
                table: "ResourceTypes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Resources_Capacity",
                table: "Resources");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RecurringSeries_EndCondition",
                table: "RecurringSeries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RecurringSeries_Interval",
                table: "RecurringSeries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Bookings_TimeRange",
                table: "Bookings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BlackoutPeriods_TimeRange",
                table: "BlackoutPeriods");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AvailabilityRules_TimeRange",
                table: "AvailabilityRules");
        }
    }
}
