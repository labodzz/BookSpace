using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantCheckInLeadMinutesAndBookingCheckInIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CheckInLeadMinutes",
                table: "Tenants",
                type: "int",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tenants_CheckInLeadMinutes",
                table: "Tenants",
                sql: "[CheckInLeadMinutes] >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_StartUtc_NoShowCandidate",
                table: "Bookings",
                column: "StartUtc",
                filter: "[Status] = 'Confirmed' AND [CheckedInAtUtc] IS NULL")
                .Annotation("SqlServer:Include", new[] { "TenantId", "UserId", "ResourceId", "EndUtc", "Quantity", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Tenants_CheckInLeadMinutes",
                table: "Tenants");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_StartUtc_NoShowCandidate",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "CheckInLeadMinutes",
                table: "Tenants");
        }
    }
}
