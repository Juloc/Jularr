using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations;

/// <summary>
/// The Home/Discover layout (#877): the instance default (one row) and the optional per-profile override. Raw tables outside the EF model like the other
/// appearance settings. Media types are stored as stable lowercase identifiers in comma-separated lists, so a media type added later never needs a
/// rewrite. A profile row exists once its onboarding was finished or skipped; its layout columns are either all set (a saved override) or all
/// NULL (the instance default applies), which the CHECK keeps true whoever writes the row.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20261008090000_HomeLayoutPreferences")]
public partial class HomeLayoutPreferences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE "InstanceHomeDefaults" (
                "Id" integer NOT NULL CONSTRAINT "PK_InstanceHomeDefaults" PRIMARY KEY CONSTRAINT "CK_InstanceHomeDefaults_SingleRow" CHECK ("Id" = 1),
                "MediaOrder" text NOT NULL,
                "HiddenMediaTypes" text NOT NULL DEFAULT '',
                "LandingPage" text NOT NULL DEFAULT 'home' CONSTRAINT "CK_InstanceHomeDefaults_Landing" CHECK ("LandingPage" IN ('home', 'library')),
                "PrioritizeContinue" boolean NOT NULL DEFAULT true,
                "UpdatedAt" timestamp with time zone NOT NULL
            );

            CREATE TABLE "ProfileHomeLayouts" (
                "ProfileId" text NOT NULL CONSTRAINT "PK_ProfileHomeLayouts" PRIMARY KEY,
                "OnboardingState" text NOT NULL CONSTRAINT "CK_ProfileHomeLayouts_Onboarding" CHECK ("OnboardingState" IN ('completed', 'skipped')),
                "MediaOrder" text NULL,
                "HiddenMediaTypes" text NULL,
                "LandingPage" text NULL CONSTRAINT "CK_ProfileHomeLayouts_Landing" CHECK ("LandingPage" IS NULL OR "LandingPage" IN ('home', 'library')),
                "PrioritizeContinue" boolean NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                CONSTRAINT "CK_ProfileHomeLayouts_Override" CHECK (
                    ("MediaOrder" IS NULL AND "HiddenMediaTypes" IS NULL AND "LandingPage" IS NULL AND "PrioritizeContinue" IS NULL)
                    OR ("MediaOrder" IS NOT NULL AND "HiddenMediaTypes" IS NOT NULL AND "LandingPage" IS NOT NULL AND "PrioritizeContinue" IS NOT NULL))
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""DROP TABLE IF EXISTS "ProfileHomeLayouts"; DROP TABLE IF EXISTS "InstanceHomeDefaults";""");
    }
}
