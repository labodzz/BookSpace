using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingReminderDueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Bookings_StartUtc",
                table: "Bookings",
                column: "StartUtc",
                filter: "[Status] = 'Confirmed'")
                .Annotation("SqlServer:Include", new[] { "TenantId", "UserId", "ResourceId", "EndUtc", "Quantity", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Bookings_StartUtc",
                table: "Bookings");
        }
    }
}
