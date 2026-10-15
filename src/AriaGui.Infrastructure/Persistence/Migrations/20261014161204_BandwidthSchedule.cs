using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AriaGui.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BandwidthSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ScheduleJson",
                table: "BandwidthPolicies",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PolicyHeld",
                table: "Attempts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScheduleJson",
                table: "BandwidthPolicies");

            migrationBuilder.DropColumn(
                name: "PolicyHeld",
                table: "Attempts");
        }
    }
}
