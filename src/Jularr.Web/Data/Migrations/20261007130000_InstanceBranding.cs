using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations;

/// <summary>
/// The instance's own branding (#876): one row with an optional display name, an optional logo and the hue branding switch. A raw table like the other
/// instance-wide settings; the CHECK constraints keep the row valid whoever writes it (one row, a bounded name, a hue on the colour wheel, a logo that
/// is either complete or absent and never larger than the upload limit).
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20261007130000_InstanceBranding")]
public partial class InstanceBranding : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE "InstanceBranding" (
                "Id" integer NOT NULL CONSTRAINT "PK_InstanceBranding" PRIMARY KEY CONSTRAINT "CK_InstanceBranding_SingleRow" CHECK ("Id" = 1),
                "Name" text NULL CONSTRAINT "CK_InstanceBranding_Name" CHECK ("Name" IS NULL OR char_length("Name") BETWEEN 1 AND 40),
                "HueBranding" boolean NOT NULL DEFAULT false,
                "Hue" smallint NULL CONSTRAINT "CK_InstanceBranding_Hue" CHECK ("Hue" IS NULL OR "Hue" BETWEEN 0 AND 359),
                "LogoContentType" text NULL,
                "Logo" bytea NULL,
                "LogoVersion" bigint NOT NULL DEFAULT 0,
                "UpdatedAt" timestamp with time zone NOT NULL,
                CONSTRAINT "CK_InstanceBranding_Logo" CHECK (("Logo" IS NULL) = ("LogoContentType" IS NULL) AND ("Logo" IS NULL OR octet_length("Logo") BETWEEN 1 AND 524288))
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""DROP TABLE IF EXISTS "InstanceBranding";""");
    }
}
