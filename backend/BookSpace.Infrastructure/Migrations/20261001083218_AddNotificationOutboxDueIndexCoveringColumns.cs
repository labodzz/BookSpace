using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationOutboxDueIndexCoveringColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NotificationOutboxItems_AvailableAtUtc",
                table: "NotificationOutboxItems");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutboxItems_AvailableAtUtc",
                table: "NotificationOutboxItems",
                column: "AvailableAtUtc",
                filter: "[Status] = 'Pending'")
                .Annotation("SqlServer:Include", new[] { "TenantId", "NotificationType", "RecipientUserId", "PayloadJson", "CreatedAtUtc", "AttemptCount" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NotificationOutboxItems_AvailableAtUtc",
                table: "NotificationOutboxItems");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutboxItems_AvailableAtUtc",
                table: "NotificationOutboxItems",
                column: "AvailableAtUtc",
                filter: "[Status] = 'Pending'");
        }
    }
}
