using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AriaGui.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class QueueCommands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "QueuePosition",
                table: "Operations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "QueuePolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Limit = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestedLimit = table.Column<int>(type: "INTEGER", nullable: true),
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RequestStatus = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueuePolicies", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QueuePolicies");

            migrationBuilder.DropColumn(
                name: "QueuePosition",
                table: "Operations");
        }
    }
}
