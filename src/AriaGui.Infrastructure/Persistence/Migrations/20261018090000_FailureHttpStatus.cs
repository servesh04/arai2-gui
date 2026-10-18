using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AriaGui.Infrastructure.Persistence.Migrations;

[DbContext(typeof(DownloadDbContext))]
[Migration("20261018090000_FailureHttpStatus")]
public sealed class FailureHttpStatus : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<int>(
        name: "HttpStatus", table: "Attempts", type: "INTEGER", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn(
        name: "HttpStatus", table: "Attempts");
}
