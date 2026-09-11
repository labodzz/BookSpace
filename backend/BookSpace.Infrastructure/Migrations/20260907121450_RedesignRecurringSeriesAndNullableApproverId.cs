using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RedesignRecurringSeriesAndNullableApproverId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RecurringSeries_TimeRange",
                table: "RecurringSeries");

            migrationBuilder.DropColumn(
                name: "EndUtc",
                table: "RecurringSeries");

            migrationBuilder.DropColumn(
                name: "StartUtc",
                table: "RecurringSeries");

            migrationBuilder.AddColumn<DateOnly>(
                name: "EndDate",
                table: "RecurringSeries",
                type: "date",
                nullable: true);

            // Defaults below are chosen to satisfy CK_RecurringSeries_TimeRange/Quantity/EndCondition
            // (added later in this same Up()) for any row that already exists at migration time -
            // EF's ADD COLUMN backfills these defaults into existing rows before the CHECK constraints
            // are added, and SQL Server validates new CHECK constraints against existing data by
            // default. RecurringSeries had zero Application-layer writers before this migration (see
            // docs/next-work-packet-handoff.md), so no real series data is expected to exist, but the
            // defaults must not contradict the constraints being added in the same migration regardless.
            migrationBuilder.AddColumn<TimeOnly>(
                name: "EndTime",
                table: "RecurringSeries",
                type: "time",
                nullable: false,
                defaultValue: new TimeOnly(23, 59, 0));

            migrationBuilder.AddColumn<int>(
                name: "OccurrenceCount",
                table: "RecurringSeries",
                type: "int",
                nullable: true,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "Quantity",
                table: "RecurringSeries",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateOnly>(
                name: "StartDate",
                table: "RecurringSeries",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.AddColumn<TimeOnly>(
                name: "StartTime",
                table: "RecurringSeries",
                type: "time",
                nullable: false,
                defaultValue: new TimeOnly(0, 0, 0));

            migrationBuilder.AlterColumn<Guid>(
                name: "ApproverId",
                table: "ApprovalRequests",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            // SQL Server does NOT backfill an existing row's nullable column with its new DEFAULT
            // constraint on a plain ADD COLUMN (unlike a NOT NULL column, which it backfills
            // automatically) - WITH VALUES would be needed for that, which the migration-generator
            // tooling doesn't emit here. OccurrenceCount is nullable, so an explicit backfill is required
            // to keep any pre-existing row from ending up with EndDate/OccurrenceCount both NULL, which
            // CK_RecurringSeries_EndCondition (added right below) would then reject.
            migrationBuilder.Sql("UPDATE RecurringSeries SET OccurrenceCount = 1 WHERE OccurrenceCount IS NULL AND EndDate IS NULL;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RecurringSeries_EndCondition",
                table: "RecurringSeries",
                sql: "([EndDate] IS NOT NULL AND [OccurrenceCount] IS NULL) OR ([EndDate] IS NULL AND [OccurrenceCount] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RecurringSeries_OccurrenceCount",
                table: "RecurringSeries",
                sql: "[OccurrenceCount] IS NULL OR [OccurrenceCount] > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RecurringSeries_Quantity",
                table: "RecurringSeries",
                sql: "[Quantity] > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RecurringSeries_TimeRange",
                table: "RecurringSeries",
                sql: "[EndTime] > [StartTime]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RecurringSeries_EndCondition",
                table: "RecurringSeries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RecurringSeries_OccurrenceCount",
                table: "RecurringSeries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RecurringSeries_Quantity",
                table: "RecurringSeries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RecurringSeries_TimeRange",
                table: "RecurringSeries");

            migrationBuilder.DropColumn(
                name: "EndDate",
                table: "RecurringSeries");

            migrationBuilder.DropColumn(
                name: "EndTime",
                table: "RecurringSeries");

            migrationBuilder.DropColumn(
                name: "OccurrenceCount",
                table: "RecurringSeries");

            migrationBuilder.DropColumn(
                name: "Quantity",
                table: "RecurringSeries");

            migrationBuilder.DropColumn(
                name: "StartDate",
                table: "RecurringSeries");

            migrationBuilder.DropColumn(
                name: "StartTime",
                table: "RecurringSeries");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EndUtc",
                table: "RecurringSeries",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartUtc",
                table: "RecurringSeries",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AlterColumn<Guid>(
                name: "ApproverId",
                table: "ApprovalRequests",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_RecurringSeries_TimeRange",
                table: "RecurringSeries",
                sql: "[EndUtc] IS NULL OR [EndUtc] > [StartUtc]");
        }
    }
}
