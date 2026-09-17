using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Migrations
{
    /// <inheritdoc />
    // ResourceTypesAreTenantOwned added ResourceTypes.TenantId as NOT NULL with a Guid.Empty default,
    // with no backfill - safe only for a database created fresh after that migration (DevelopmentSeeder
    // always writes a real TenantId). A database that already had ResourceType rows from BEFORE that
    // migration existed (ResourceType was originally tenant-less, since InitialCreate) would have those
    // rows silently stuck at TenantId = Guid.Empty forever, invisible under the global tenant query
    // filter even though Resources rows still reference them via ResourceTypeId. This repairs any such
    // row by deriving its real TenantId from the Resources row that references it - the same relationship
    // DevelopmentSeeder itself relies on to keep the two in sync.
    public partial class BackfillResourceTypesTenantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE rt
SET rt.TenantId = r.TenantId
FROM ResourceTypes rt
INNER JOIN Resources r ON r.ResourceTypeId = rt.Id
WHERE rt.TenantId = '00000000-0000-0000-0000-000000000000';
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible - there is no way to recover which rows were originally at Guid.Empty
            // versus genuinely (if implausibly) owned by a real tenant with that id.
        }
    }
}
