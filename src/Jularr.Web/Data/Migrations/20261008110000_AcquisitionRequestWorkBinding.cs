using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations;

/// <summary>
/// The canonical Work a request targets (#396, #432). Nullable on purpose: a request made before the binding, and a request whose identity could not be
/// resolved safely, stay valid and are bound lazily; nothing is rewritten. It is plain text like the other ids of this raw table and carries no
/// foreign key, because a Work may be merged or removed by its own owner and the request must outlive that.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20261008110000_AcquisitionRequestWorkBinding")]
public partial class AcquisitionRequestWorkBinding : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE "AcquisitionRequests" ADD COLUMN "WorkId" TEXT NULL;
            CREATE INDEX "IX_AcquisitionRequests_WorkId" ON "AcquisitionRequests" ("WorkId") WHERE "WorkId" IS NOT NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_AcquisitionRequests_WorkId"; ALTER TABLE "AcquisitionRequests" DROP COLUMN "WorkId";""");
    }
}
