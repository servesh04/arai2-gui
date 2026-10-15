using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AriaGui.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BandwidthPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BandwidthPolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RequestedRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    ConfirmedRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestedName = table.Column<string>(type: "TEXT", nullable: false),
                    RequestedDownloadBytesPerSecond = table.Column<long>(type: "INTEGER", nullable: true),
                    RequestedUploadBytesPerSecond = table.Column<long>(type: "INTEGER", nullable: true),
                    ConfirmedName = table.Column<string>(type: "TEXT", nullable: false),
                    ConfirmedDownloadBytesPerSecond = table.Column<long>(type: "INTEGER", nullable: true),
                    ConfirmedUploadBytesPerSecond = table.Column<long>(type: "INTEGER", nullable: true),
                    ConfirmedGeneration = table.Column<string>(type: "TEXT", nullable: true),
                    RequestStatus = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BandwidthPolicies", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BandwidthPolicies");
        }
    }
}
