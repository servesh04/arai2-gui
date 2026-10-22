using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AriaGui.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NotificationStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    TransferKind = table.Column<int>(type: "INTEGER", nullable: true),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AttemptId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    CreatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    ReadAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    DismissedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    DeliveryState = table.Column<int>(type: "INTEGER", nullable: false),
                    DeliveryUpdatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationReceipts",
                columns: table => new
                {
                    EventKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    CreatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationReceipts", x => x.EventKey);
                });

            migrationBuilder.CreateTable(
                name: "NotificationSourceRevisions",
                columns: table => new
                {
                    SourceKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationSourceRevisions", x => x.SourceKey);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationEvents_DismissedAtUtcTicks_Id",
                table: "NotificationEvents",
                columns: new[] { "DismissedAtUtcTicks", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationEvents_EventKey",
                table: "NotificationEvents",
                column: "EventKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationEvents_ReadAtUtcTicks_Id",
                table: "NotificationEvents",
                columns: new[] { "ReadAtUtcTicks", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationEvents");

            migrationBuilder.DropTable(
                name: "NotificationReceipts");

            migrationBuilder.DropTable(
                name: "NotificationSourceRevisions");
        }
    }
}
