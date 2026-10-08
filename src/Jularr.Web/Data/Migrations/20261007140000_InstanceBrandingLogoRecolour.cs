using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations;

/// <summary>
/// Logo recolouring (#876): whether the owner wants the logo shown in the brand colour, and whether the uploaded logo can be (it has a real alpha channel
/// to take the silhouette from). Recolouring is presentation only; the uploaded bytes are never changed.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20261007140000_InstanceBrandingLogoRecolour")]
public partial class InstanceBrandingLogoRecolour : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE "InstanceBranding"
                ADD COLUMN "RecolourLogo" boolean NOT NULL DEFAULT false,
                ADD COLUMN "LogoRecolourable" boolean NOT NULL DEFAULT false;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""ALTER TABLE "InstanceBranding" DROP COLUMN "RecolourLogo", DROP COLUMN "LogoRecolourable";""");
    }
}
