using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261004152000_ReaderImagePreferences")]
public partial class ReaderImagePreferences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ImageFlowMode",
            table: "ReaderPreferences",
            type: "character varying(24)",
            maxLength: 24,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ImagePageDirection",
            table: "ReaderPreferences",
            type: "character varying(16)",
            maxLength: 16,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ImageFit",
            table: "ReaderPreferences",
            type: "character varying(16)",
            maxLength: 16,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "ImageZoomPercent",
            table: "ReaderPreferences",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "ImagePageGapPx",
            table: "ReaderPreferences",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "ImageFirstPageAlone",
            table: "ReaderPreferences",
            type: "boolean",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "ImageSharpen",
            table: "ReaderPreferences",
            type: "boolean",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "ImageCropBorders",
            table: "ReaderPreferences",
            type: "boolean",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ImageColorScheme",
            table: "ReaderPreferences",
            type: "character varying(16)",
            maxLength: 16,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ImageFlowMode", table: "ReaderPreferences");
        migrationBuilder.DropColumn(name: "ImagePageDirection", table: "ReaderPreferences");
        migrationBuilder.DropColumn(name: "ImageFit", table: "ReaderPreferences");
        migrationBuilder.DropColumn(name: "ImageZoomPercent", table: "ReaderPreferences");
        migrationBuilder.DropColumn(name: "ImagePageGapPx", table: "ReaderPreferences");
        migrationBuilder.DropColumn(name: "ImageFirstPageAlone", table: "ReaderPreferences");
        migrationBuilder.DropColumn(name: "ImageSharpen", table: "ReaderPreferences");
        migrationBuilder.DropColumn(name: "ImageCropBorders", table: "ReaderPreferences");
        migrationBuilder.DropColumn(name: "ImageColorScheme", table: "ReaderPreferences");
    }
}
