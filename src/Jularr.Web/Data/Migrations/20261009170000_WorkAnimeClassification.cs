using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkAnimeClassification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAnime",
                table: "Works",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Works_IsAnime",
                table: "Works",
                column: "IsAnime",
                filter: "\"IsAnime\"");

            // Anime is a classification of a Series now: the Works of the old Anime type keep their ids, links, identities and structure and become classified Series Works.
            migrationBuilder.Sql(
                """
                UPDATE "Works" SET "MediaType" = 1, "IsAnime" = TRUE WHERE "MediaType" = 2;

                INSERT INTO "WorkFieldProvenance" ("Id", "WorkId", "FieldKey", "Source", "ProviderExternalId", "Confidence", "IsManualOverride", "FallbackPriority", "FetchedAt", "UpdatedAt")
                SELECT gen_random_uuid(), work."Id", 'classification.anime', 'anime-library', NULL, 1.0, FALSE, 20, now(), now()
                FROM "Works" work
                WHERE work."IsAnime"
                  AND NOT EXISTS (SELECT 1 FROM "WorkFieldProvenance" known WHERE known."WorkId" = work."Id" AND known."FieldKey" = 'classification.anime');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE "Works" SET "MediaType" = 2 WHERE "IsAnime" AND "MediaType" = 1;""");

            migrationBuilder.DropIndex(
                name: "IX_Works_IsAnime",
                table: "Works");

            migrationBuilder.DropColumn(
                name: "IsAnime",
                table: "Works");
        }
    }
}
