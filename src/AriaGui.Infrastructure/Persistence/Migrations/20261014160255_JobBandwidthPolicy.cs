using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AriaGui.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class JobBandwidthPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobBandwidthPolicies",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestedRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    ConfirmedRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestedDownloadBytesPerSecond = table.Column<long>(type: "INTEGER", nullable: true),
                    ConfirmedDownloadBytesPerSecond = table.Column<long>(type: "INTEGER", nullable: true),
                    ConfirmedGeneration = table.Column<string>(type: "TEXT", nullable: true),
                    RequestStatus = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobBandwidthPolicies", x => x.JobId);
                    table.ForeignKey(
                        name: "FK_JobBandwidthPolicies_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobBandwidthPolicies");
        }
    }
}
