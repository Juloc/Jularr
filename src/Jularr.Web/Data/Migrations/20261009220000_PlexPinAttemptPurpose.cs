using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261009220000_PlexPinAttemptPurpose")]
public sealed class PlexPinAttemptPurpose : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Purpose",
            table: "PlexLoginAttempts",
            type: "character varying(12)",
            maxLength: 12,
            nullable: false,
            defaultValue: "login");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Purpose",
            table: "PlexLoginAttempts");
    }
}
