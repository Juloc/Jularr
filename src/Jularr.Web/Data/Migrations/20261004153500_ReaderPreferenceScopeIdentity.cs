using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261004153500_ReaderPreferenceScopeIdentity")]
public partial class ReaderPreferenceScopeIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_ReaderPreferences_NovelWorks_WorkId",
            table: "ReaderPreferences");

        migrationBuilder.DropIndex(
            name: "IX_ReaderPreferences_WorkId",
            table: "ReaderPreferences");

        migrationBuilder.DropColumn(
            name: "WorkId",
            table: "ReaderPreferences");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "WorkId",
            table: "ReaderPreferences",
            type: "uuid",
            nullable: true);

        migrationBuilder.Sql(
            """
            UPDATE "ReaderPreferences" AS Preference
            SET "WorkId" = substring(
                Preference."ScopeKey"
                FROM char_length('work:') + 1)::uuid
            WHERE Preference."ScopeKey" LIKE 'work:%'
              AND substring(
                    Preference."ScopeKey"
                    FROM char_length('work:') + 1
                  ) ~ '^[0-9a-fA-F]{32}$'
              AND EXISTS (
                  SELECT 1
                  FROM "NovelWorks" AS NovelWork
                  WHERE NovelWork."Id" = substring(
                      Preference."ScopeKey"
                      FROM char_length('work:') + 1
                  )::uuid
              );
            """);

        migrationBuilder.CreateIndex(
            name: "IX_ReaderPreferences_WorkId",
            table: "ReaderPreferences",
            column: "WorkId");

        migrationBuilder.AddForeignKey(
            name: "FK_ReaderPreferences_NovelWorks_WorkId",
            table: "ReaderPreferences",
            column: "WorkId",
            principalTable: "NovelWorks",
            principalColumn: "Id",
            onDelete: ReferentialAction.NoAction);
    }
}
