using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingReminderQueuedAtUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Bookings_StartUtc",
                table: "Bookings");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReminderQueuedAtUtc",
                table: "Bookings",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_StartUtc",
                table: "Bookings",
                column: "StartUtc",
                filter: "[Status] = 'Confirmed' AND [ReminderQueuedAtUtc] IS NULL")
                .Annotation("SqlServer:Include", new[] { "TenantId", "UserId", "ResourceId", "EndUtc", "Quantity", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Bookings_StartUtc",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "ReminderQueuedAtUtc",
                table: "Bookings");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_StartUtc",
                table: "Bookings",
                column: "StartUtc",
                filter: "[Status] = 'Confirmed'")
                .Annotation("SqlServer:Include", new[] { "TenantId", "UserId", "ResourceId", "EndUtc", "Quantity", "CreatedAtUtc" });
        }
    }
}
