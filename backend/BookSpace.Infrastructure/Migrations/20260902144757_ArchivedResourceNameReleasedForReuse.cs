using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ArchivedResourceNameReleasedForReuse : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Resources_TenantId_Name",
                table: "Resources");

            migrationBuilder.CreateIndex(
                name: "IX_Resources_TenantId_Name",
                table: "Resources",
                columns: new[] { "TenantId", "Name" },
                unique: true,
                filter: "[Status] <> 'Archived'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Resources_TenantId_Name",
                table: "Resources");

            migrationBuilder.CreateIndex(
                name: "IX_Resources_TenantId_Name",
                table: "Resources",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }
    }
}
