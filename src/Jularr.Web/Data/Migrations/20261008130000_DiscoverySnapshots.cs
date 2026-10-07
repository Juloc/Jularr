using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations;

/// <summary>
/// The local discovery snapshot: the last successful answer of every browse source (trending, top, new, upcoming of each media type and locale), so Discover
/// can show usable titles on the first response after a restart and while a provider is down. A cache, not a catalog: nothing here is a Work, the rows are
/// bounded and replaced by the next successful answer, and canonical library metadata never depends on it. Raw SQL like the other single-purpose tables.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20261008130000_DiscoverySnapshots")]
public partial class DiscoverySnapshots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE "DiscoverySnapshots" (
                "Key" TEXT NOT NULL,
                "Payload" TEXT NOT NULL,
                "FetchedAt" TIMESTAMPTZ NOT NULL,
                CONSTRAINT "PK_DiscoverySnapshots" PRIMARY KEY ("Key")
            );
            CREATE INDEX "IX_DiscoverySnapshots_FetchedAt" ON "DiscoverySnapshots" ("FetchedAt");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""DROP TABLE IF EXISTS "DiscoverySnapshots";""");
    }
}
