using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AriaGui.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecoveryIntent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DesiredPaused",
                table: "Attempts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
            // Carry forward the most recent Phase 4 intent; a pending Resume overrides
            // an old Paused observation, while a pending Pause must survive interruption.
            migrationBuilder.Sql("""
                UPDATE Attempts SET DesiredPaused = CASE
                  WHEN (SELECT Action FROM Operations WHERE AttemptId=Attempts.Id AND Status IN (0,1,2)
                        ORDER BY RequestedAtUtcTicks DESC LIMIT 1)=2 THEN 0
                  WHEN Status=2 OR (SELECT Action FROM Operations WHERE AttemptId=Attempts.Id AND Status IN (0,1,2)
                        ORDER BY RequestedAtUtcTicks DESC LIMIT 1)=1 THEN 1 ELSE 0 END;
                """);

            migrationBuilder.AddColumn<bool>(
                name: "DesiredRemoved",
                table: "Attempts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
            migrationBuilder.Sql("""
                UPDATE Attempts SET DesiredRemoved = CASE WHEN Status=5 OR
                  (SELECT Action FROM Operations WHERE AttemptId=Attempts.Id AND Status IN (0,1,2)
                   ORDER BY RequestedAtUtcTicks DESC LIMIT 1)=3 THEN 1 ELSE 0 END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DesiredPaused",
                table: "Attempts");

            migrationBuilder.DropColumn(
                name: "DesiredRemoved",
                table: "Attempts");
        }
    }
}
