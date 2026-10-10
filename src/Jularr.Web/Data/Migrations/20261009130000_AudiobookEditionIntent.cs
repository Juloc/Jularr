using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations;

// The audio edition of a Book Work is a WorkEdition of its own (key and format "audiobook", the one an audiobook import bridges as), so Monitoring can decide
// on it apart from the Book and a request names that edition instead of the Work. A monitoring decision may now name an edition, and the audiobook requests
// that named their Work as the edition target are moved onto the edition; the Wanted rows of edition targets are rebuilt by the next reconcile.
[DbContext(typeof(AppDbContext))]
[Migration("20261009130000_AudiobookEditionIntent")]
public partial class AudiobookEditionIntent : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE "WorkMonitoring" DROP CONSTRAINT "CK_WorkMonitoring_Kind";
            ALTER TABLE "WorkMonitoring" ADD CONSTRAINT "CK_WorkMonitoring_Kind" CHECK ("Kind" BETWEEN 0 AND 6);

            INSERT INTO "WorkEditions" ("Id", "WorkId", "EditionKey", "Language", "Format", "IsPrimary", "CreatedAt", "UpdatedAt")
            SELECT gen_random_uuid(), named."WorkId", 'audiobook', 'und', 'audiobook', FALSE, now(), now()
            FROM (SELECT DISTINCT target."WorkId" FROM "RequestTargets" target WHERE target."TargetKind" = 4 AND target."TargetId" = target."WorkId") named
            WHERE EXISTS (SELECT 1 FROM "Works" work WHERE work."Id" = named."WorkId")
            ON CONFLICT ("WorkId", "EditionKey") DO NOTHING;

            UPDATE "RequestTargets" target
            SET "TargetId" = edition."Id"
            FROM "WorkEditions" edition
            WHERE target."TargetKind" = 4 AND target."TargetId" = target."WorkId" AND edition."WorkId" = target."WorkId" AND edition."EditionKey" = 'audiobook';

            DELETE FROM "WantedItems" WHERE "TargetKind" = 4;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DELETE FROM "WorkMonitoring" WHERE "Kind" = 6;
            ALTER TABLE "WorkMonitoring" DROP CONSTRAINT "CK_WorkMonitoring_Kind";
            ALTER TABLE "WorkMonitoring" ADD CONSTRAINT "CK_WorkMonitoring_Kind" CHECK ("Kind" BETWEEN 0 AND 5);
            UPDATE "RequestTargets" SET "TargetId" = "WorkId" WHERE "TargetKind" = 4;
            DELETE FROM "WantedItems" WHERE "TargetKind" = 4;
            """);
    }
}
