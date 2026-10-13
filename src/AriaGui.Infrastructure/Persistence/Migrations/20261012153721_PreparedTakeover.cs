using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AriaGui.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PreparedTakeover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PreparedExpiresAtUtcTicks",
                table: "ExternalRequests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreparedIntentHash",
                table: "ExternalRequests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreparedIntentJson",
                table: "ExternalRequests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TakeoverPhase",
                table: "ExternalRequests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PreparedDestinations",
                columns: table => new
                {
                    DestinationKey = table.Column<string>(type: "TEXT", nullable: false),
                    CallerKey = table.Column<string>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreparedDestinations", x => x.DestinationKey);
                    table.ForeignKey(
                        name: "FK_PreparedDestinations_ExternalRequests_CallerKey_InstallationId_RequestId",
                        columns: x => new { x.CallerKey, x.InstallationId, x.RequestId },
                        principalTable: "ExternalRequests",
                        principalColumns: new[] { "CallerKey", "InstallationId", "RequestId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PreparedDestinations_CallerKey_InstallationId_RequestId",
                table: "PreparedDestinations",
                columns: new[] { "CallerKey", "InstallationId", "RequestId" });
            migrationBuilder.Sql("CREATE TRIGGER Jobs_ReservedDestinationInsert BEFORE INSERT ON Jobs WHEN EXISTS (SELECT 1 FROM PreparedDestinations WHERE DestinationKey = NEW.DestinationKey) BEGIN SELECT RAISE(ABORT, 'prepared destination conflict'); END;");
            migrationBuilder.Sql("CREATE TRIGGER Jobs_ReservedDestinationUpdate BEFORE UPDATE OF DestinationKey ON Jobs WHEN EXISTS (SELECT 1 FROM PreparedDestinations WHERE DestinationKey = NEW.DestinationKey) BEGIN SELECT RAISE(ABORT, 'prepared destination conflict'); END;");
            migrationBuilder.Sql("CREATE TRIGGER PreparedDestinations_ExistingJob BEFORE INSERT ON PreparedDestinations WHEN EXISTS (SELECT 1 FROM Jobs WHERE DestinationKey = NEW.DestinationKey) BEGIN SELECT RAISE(ABORT, 'existing destination conflict'); END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER Jobs_ReservedDestinationInsert;");
            migrationBuilder.Sql("DROP TRIGGER Jobs_ReservedDestinationUpdate;");
            migrationBuilder.Sql("DROP TRIGGER PreparedDestinations_ExistingJob;");
            migrationBuilder.DropTable(
                name: "PreparedDestinations");

            migrationBuilder.DropColumn(
                name: "PreparedExpiresAtUtcTicks",
                table: "ExternalRequests");

            migrationBuilder.DropColumn(
                name: "PreparedIntentHash",
                table: "ExternalRequests");

            migrationBuilder.DropColumn(
                name: "PreparedIntentJson",
                table: "ExternalRequests");

            migrationBuilder.DropColumn(
                name: "TakeoverPhase",
                table: "ExternalRequests");
        }
    }
}
