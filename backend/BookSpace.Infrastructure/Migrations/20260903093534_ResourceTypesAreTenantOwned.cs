using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ResourceTypesAreTenantOwned : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ResourceTypes_Name",
                table: "ResourceTypes");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "ResourceTypes",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_ResourceTypes_TenantId_Name",
                table: "ResourceTypes",
                columns: new[] { "TenantId", "Name" },
                unique: true);

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

            migrationBuilder.DropIndex(
                name: "IX_ResourceTypes_TenantId_Name",
                table: "ResourceTypes");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "ResourceTypes");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceTypes_Name",
                table: "ResourceTypes",
                column: "Name",
                unique: true);
        }
    }
}
