using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AriaGui.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TorrentStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TorrentSpecs",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InfoHash = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Metainfo = table.Column<byte[]>(type: "BLOB", nullable: false),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    PieceLength = table.Column<long>(type: "INTEGER", nullable: false),
                    IsPrivate = table.Column<bool>(type: "INTEGER", nullable: false),
                    PayloadRoot = table.Column<string>(type: "TEXT", nullable: false),
                    DestinationRoot = table.Column<string>(type: "TEXT", nullable: false),
                    DestinationKey = table.Column<string>(type: "TEXT", nullable: false),
                    SelectedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Stage = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TorrentSpecs", x => x.JobId);
                });

            migrationBuilder.CreateTable(
                name: "TorrentEngines",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    Generation = table.Column<string>(type: "TEXT", nullable: false),
                    EngineId = table.Column<string>(type: "TEXT", nullable: false),
                    InfoHash = table.Column<string>(type: "TEXT", nullable: false),
                    RegisteredAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TorrentEngines", x => new { x.JobId, x.Role });
                    table.ForeignKey(
                        name: "FK_TorrentEngines_TorrentSpecs_JobId",
                        column: x => x.JobId,
                        principalTable: "TorrentSpecs",
                        principalColumn: "JobId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TorrentFiles",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    Length = table.Column<long>(type: "INTEGER", nullable: false),
                    Selected = table.Column<bool>(type: "INTEGER", nullable: false),
                    PublishedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Published = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TorrentFiles", x => new { x.JobId, x.FileIndex });
                    table.ForeignKey(
                        name: "FK_TorrentFiles_TorrentSpecs_JobId",
                        column: x => x.JobId,
                        principalTable: "TorrentSpecs",
                        principalColumn: "JobId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TorrentEngines_Generation_EngineId",
                table: "TorrentEngines",
                columns: new[] { "Generation", "EngineId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TorrentSpecs_DestinationKey",
                table: "TorrentSpecs",
                column: "DestinationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TorrentSpecs_InfoHash",
                table: "TorrentSpecs",
                column: "InfoHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TorrentEngines");

            migrationBuilder.DropTable(
                name: "TorrentFiles");

            migrationBuilder.DropTable(
                name: "TorrentSpecs");
        }
    }
}
