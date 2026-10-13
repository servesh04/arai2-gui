using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AriaGui.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PreparedTakeoverBounds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Pre-envelope reservations cannot authorize a commit after upgrade. Accepted jobs
            // and terminal receipts are unaffected; the migration backup retains old bytes.
            migrationBuilder.Sql("DELETE FROM PreparedDestinations WHERE EXISTS (SELECT 1 FROM ExternalRequests r WHERE r.CallerKey=PreparedDestinations.CallerKey AND r.InstallationId=PreparedDestinations.InstallationId AND r.RequestId=PreparedDestinations.RequestId AND r.TakeoverPhase=0);");
            migrationBuilder.Sql("UPDATE ExternalRequests SET TakeoverPhase=3, Code=5, TombstonedAtUtcTicks=PreparedExpiresAtUtcTicks, PreparedIntentJson=NULL, PreparedIntentHash=NULL WHERE TakeoverPhase=0;");
            migrationBuilder.CreateIndex(
                name: "IX_ExternalRequests_CallerKey_TakeoverPhase_InstallationId",
                table: "ExternalRequests",
                columns: new[] { "CallerKey", "TakeoverPhase", "InstallationId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalRequests_InstallationId_TakeoverPhase",
                table: "ExternalRequests",
                columns: new[] { "InstallationId", "TakeoverPhase" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalRequests_TakeoverPhase_PreparedExpiresAtUtcTicks",
                table: "ExternalRequests",
                columns: new[] { "TakeoverPhase", "PreparedExpiresAtUtcTicks" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExternalRequests_CallerKey_TakeoverPhase_InstallationId",
                table: "ExternalRequests");

            migrationBuilder.DropIndex(
                name: "IX_ExternalRequests_InstallationId_TakeoverPhase",
                table: "ExternalRequests");

            migrationBuilder.DropIndex(
                name: "IX_ExternalRequests_TakeoverPhase_PreparedExpiresAtUtcTicks",
                table: "ExternalRequests");
        }
    }
}
