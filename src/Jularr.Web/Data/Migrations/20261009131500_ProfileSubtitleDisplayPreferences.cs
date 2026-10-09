using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261009131500_ProfileSubtitleDisplayPreferences")]
public partial class ProfileSubtitleDisplayPreferences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PreferredSecondarySubtitleLanguage",
            table: "ProfilePlaybackPreferences",
            type: "character varying(16)",
            maxLength: 16,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "SubtitleSizePercent",
            table: "ProfilePlaybackPreferences",
            type: "integer",
            nullable: false,
            defaultValue: 100);

        migrationBuilder.AddColumn<int>(
            name: "SubtitleOffsetMs",
            table: "ProfilePlaybackPreferences",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddCheckConstraint(
            name: "CK_ProfilePlaybackPreferences_SubtitleSizePercent",
            table: "ProfilePlaybackPreferences",
            sql: "\"SubtitleSizePercent\" BETWEEN 75 AND 200");

        migrationBuilder.AddCheckConstraint(
            name: "CK_ProfilePlaybackPreferences_SubtitleOffsetMs",
            table: "ProfilePlaybackPreferences",
            sql: "\"SubtitleOffsetMs\" BETWEEN -10000 AND 10000");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "PreferredSecondarySubtitleLanguage", table: "ProfilePlaybackPreferences");
        migrationBuilder.DropColumn(name: "SubtitleSizePercent", table: "ProfilePlaybackPreferences");
        migrationBuilder.DropColumn(name: "SubtitleOffsetMs", table: "ProfilePlaybackPreferences");
    }
}
