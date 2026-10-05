using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddApprovalRequestExpiresAtUtcIndexAndStatuses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_ExpiresAtUtc",
                table: "ApprovalRequests",
                column: "ExpiresAtUtc",
                filter: "[Status] = 'Pending'")
                .Annotation("SqlServer:Include", new[] { "TenantId", "BookingId", "RequestedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ApprovalRequests_ExpiresAtUtc",
                table: "ApprovalRequests");
        }
    }
}
