using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261004154500_CanonicalMangaReaderPreferenceScopes")]
public partial class CanonicalMangaReaderPreferenceScopes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            INSERT INTO "Works" (
                "Id", "MediaType", "CanonicalTitle", "Year", "CreatedAt", "UpdatedAt")
            SELECT
                Series."Id"::uuid,
                4,
                COALESCE(NULLIF(Series."MetadataTitle", ''), Series."Title"),
                NULL,
                CURRENT_TIMESTAMP,
                CURRENT_TIMESTAMP
            FROM "MangaSeries" AS Series
            WHERE EXISTS (
                SELECT 1
                FROM "ReaderPreferences" AS Preference
                WHERE Preference."ScopeKey" =
                    'media:manga:series:' || replace(Series."Id"::text, '-', '')
            )
              AND NOT EXISTS (
                SELECT 1
                FROM "WorkSourceLinks" AS SourceLink
                WHERE SourceLink."SourceKind" = 3
                  AND SourceLink."SourceId" = Series."Id"::uuid
            )
              AND NOT EXISTS (
                SELECT 1
                FROM "Works" AS ExistingWork
                WHERE ExistingWork."Id" = Series."Id"::uuid
            );
            """);

        migrationBuilder.Sql(
            """
            INSERT INTO "WorkSourceLinks" (
                "Id", "WorkId", "SourceKind", "SourceId", "CreatedAt")
            SELECT
                gen_random_uuid(),
                Series."Id"::uuid,
                3,
                Series."Id"::uuid,
                CURRENT_TIMESTAMP
            FROM "MangaSeries" AS Series
            JOIN "Works" AS Work
              ON Work."Id" = Series."Id"::uuid
             AND Work."MediaType" = 4
            WHERE EXISTS (
                SELECT 1
                FROM "ReaderPreferences" AS Preference
                WHERE Preference."ScopeKey" =
                    'media:manga:series:' || replace(Series."Id"::text, '-', '')
            )
              AND NOT EXISTS (
                SELECT 1
                FROM "WorkSourceLinks" AS SourceLink
                WHERE SourceLink."SourceKind" = 3
                  AND SourceLink."SourceId" = Series."Id"::uuid
            );
            """);

        migrationBuilder.Sql(
            """
            UPDATE "ReaderPreferences" AS Target
            SET
                "ReadingMode" = COALESCE(Target."ReadingMode", Legacy."ReadingMode"),
                "PageTransition" = COALESCE(Target."PageTransition", Legacy."PageTransition"),
                "TwoPageSpread" = COALESCE(Target."TwoPageSpread", Legacy."TwoPageSpread"),
                "BookmarkColor" = COALESCE(Target."BookmarkColor", Legacy."BookmarkColor"),
                "ImageFlowMode" = COALESCE(Target."ImageFlowMode", Legacy."ImageFlowMode"),
                "ImagePageDirection" = COALESCE(Target."ImagePageDirection", Legacy."ImagePageDirection"),
                "ImageFit" = COALESCE(Target."ImageFit", Legacy."ImageFit"),
                "ImageZoomPercent" = COALESCE(Target."ImageZoomPercent", Legacy."ImageZoomPercent"),
                "ImagePageGapPx" = COALESCE(Target."ImagePageGapPx", Legacy."ImagePageGapPx"),
                "ImageFirstPageAlone" = COALESCE(Target."ImageFirstPageAlone", Legacy."ImageFirstPageAlone"),
                "AutoContinueChapters" = COALESCE(Target."AutoContinueChapters", Legacy."AutoContinueChapters"),
                "ImageSharpen" = COALESCE(Target."ImageSharpen", Legacy."ImageSharpen"),
                "ImageCropBorders" = COALESCE(Target."ImageCropBorders", Legacy."ImageCropBorders"),
                "ImageColorScheme" = COALESCE(Target."ImageColorScheme", Legacy."ImageColorScheme")
            FROM "ReaderPreferences" AS Legacy
            WHERE Legacy."ProfileId" = Target."ProfileId"
              AND Legacy."ScopeKey" = 'media:manga'
              AND Target."ScopeKey" = 'type:manga';

            DELETE FROM "ReaderPreferences" AS Legacy
            USING "ReaderPreferences" AS Target
            WHERE Legacy."ProfileId" = Target."ProfileId"
              AND Legacy."ScopeKey" = 'media:manga'
              AND Target."ScopeKey" = 'type:manga';

            UPDATE "ReaderPreferences"
            SET "ScopeKey" = 'type:manga'
            WHERE "ScopeKey" = 'media:manga';
            """);

        migrationBuilder.Sql(
            """
            UPDATE "ReaderPreferences" AS Target
            SET
                "ReadingMode" = COALESCE(Target."ReadingMode", Legacy."ReadingMode"),
                "PageTransition" = COALESCE(Target."PageTransition", Legacy."PageTransition"),
                "TwoPageSpread" = COALESCE(Target."TwoPageSpread", Legacy."TwoPageSpread"),
                "BookmarkColor" = COALESCE(Target."BookmarkColor", Legacy."BookmarkColor"),
                "ImageFlowMode" = COALESCE(Target."ImageFlowMode", Legacy."ImageFlowMode"),
                "ImagePageDirection" = COALESCE(Target."ImagePageDirection", Legacy."ImagePageDirection"),
                "ImageFit" = COALESCE(Target."ImageFit", Legacy."ImageFit"),
                "ImageZoomPercent" = COALESCE(Target."ImageZoomPercent", Legacy."ImageZoomPercent"),
                "ImagePageGapPx" = COALESCE(Target."ImagePageGapPx", Legacy."ImagePageGapPx"),
                "ImageFirstPageAlone" = COALESCE(Target."ImageFirstPageAlone", Legacy."ImageFirstPageAlone"),
                "AutoContinueChapters" = COALESCE(Target."AutoContinueChapters", Legacy."AutoContinueChapters"),
                "ImageSharpen" = COALESCE(Target."ImageSharpen", Legacy."ImageSharpen"),
                "ImageCropBorders" = COALESCE(Target."ImageCropBorders", Legacy."ImageCropBorders"),
                "ImageColorScheme" = COALESCE(Target."ImageColorScheme", Legacy."ImageColorScheme")
            FROM "ReaderPreferences" AS Legacy
            JOIN "MangaSeries" AS Series
              ON Legacy."ScopeKey" =
                 'media:manga:series:' || replace(Series."Id"::text, '-', '')
            JOIN "WorkSourceLinks" AS SourceLink
              ON SourceLink."SourceKind" = 3
             AND SourceLink."SourceId" = Series."Id"::uuid
            WHERE Target."ProfileId" = Legacy."ProfileId"
              AND Target."ScopeKey" =
                  'work:' || replace(SourceLink."WorkId"::text, '-', '');

            DELETE FROM "ReaderPreferences" AS Legacy
            USING "MangaSeries" AS Series,
                  "WorkSourceLinks" AS SourceLink,
                  "ReaderPreferences" AS Target
            WHERE Legacy."ScopeKey" =
                  'media:manga:series:' || replace(Series."Id"::text, '-', '')
              AND SourceLink."SourceKind" = 3
              AND SourceLink."SourceId" = Series."Id"::uuid
              AND Target."ProfileId" = Legacy."ProfileId"
              AND Target."ScopeKey" =
                  'work:' || replace(SourceLink."WorkId"::text, '-', '');
            """);

        migrationBuilder.Sql(
            """
            UPDATE "ReaderPreferences" AS Preference
            SET "ScopeKey" = 'work:' || replace(SourceLink."WorkId"::text, '-', '')
            FROM "MangaSeries" AS Series
            JOIN "WorkSourceLinks" AS SourceLink
              ON SourceLink."SourceKind" = 3
             AND SourceLink."SourceId" = Series."Id"::uuid
            WHERE Preference."ScopeKey" =
                  'media:manga:series:' || replace(Series."Id"::text, '-', '')
              AND NOT EXISTS (
                  SELECT 1
                  FROM "ReaderPreferences" AS Target
                  WHERE Target."ProfileId" = Preference."ProfileId"
                    AND Target."ScopeKey" =
                        'work:' || replace(SourceLink."WorkId"::text, '-', '')
              );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            UPDATE "ReaderPreferences" AS Preference
            SET "ScopeKey" =
                'media:manga:series:' || replace(SourceLink."SourceId"::text, '-', '')
            FROM "WorkSourceLinks" AS SourceLink
            WHERE SourceLink."SourceKind" = 3
              AND Preference."ScopeKey" =
                  'work:' || replace(SourceLink."WorkId"::text, '-', '');

            UPDATE "ReaderPreferences"
            SET "ScopeKey" = 'media:manga'
            WHERE "ScopeKey" = 'type:manga';
            """);
    }
}
