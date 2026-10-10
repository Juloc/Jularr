using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class MangaChapterVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "MangaChapters" ADD COLUMN "SupersededById" TEXT NULL
                    CONSTRAINT "FK_MangaChapters_MangaChapters_SupersededById" REFERENCES "MangaChapters" ("Id") ON DELETE SET NULL;
                CREATE INDEX "IX_MangaChapters_SupersededById" ON "MangaChapters" ("SupersededById");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""ALTER TABLE "MangaChapters" DROP COLUMN "SupersededById";""");
        }
    }
}
