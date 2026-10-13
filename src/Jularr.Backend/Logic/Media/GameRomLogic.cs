using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    internal static class GameRomLogic
    {
        public static Task<GameRomCatalogStateData?> ReadGameRomCatalogAsync(this LogicContext ctx, GameRomCatalogNameData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    catalog."ImportedAt"
                FROM
                    "GameRomCatalogs" AS catalog
                WHERE
                    catalog."Name" = @Name;
                """;
            sql.Parameters.Add(data);
            return ctx.ReadOptionalAsync<GameRomCatalogStateData>(sql, ct);
        }

        public static async Task SaveGameRomCatalogAsync(this LogicContext ctx, GameRomCatalogData data, CancellationToken ct)
        {
            // The DAT's platform joins the one a provider already named.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH platform AS (
                    INSERT INTO
                        "GamePlatforms" ("IgdbId", "Name", "Abbreviation")
                    VALUES
                        (@IgdbId, @PlatformName, @Abbreviation)
                    ON CONFLICT ("IgdbId") DO UPDATE
                    SET
                        "Abbreviation" = COALESCE("GamePlatforms"."Abbreviation", EXCLUDED."Abbreviation")
                    RETURNING
                        "Id"
                )
                INSERT INTO
                    "GameRomCatalogs" ("Name", "GamePlatformId", "Version", "ImportedAt")
                SELECT
                    @Name,
                    platform."Id",
                    @Version,
                    @ImportedAt
                FROM
                    platform
                ON CONFLICT ("Name") DO UPDATE
                SET
                    "GamePlatformId" = EXCLUDED."GamePlatformId",
                    "Version" = EXCLUDED."Version",
                    "ImportedAt" = EXCLUDED."ImportedAt";
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "GameRoms" AS rom
                WHERE
                    rom."GameRomCatalogId" = (SELECT catalog."Id" FROM "GameRomCatalogs" AS catalog WHERE catalog."Name" = @Name);
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            // A dump listed under several names keeps its first.
            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "GameRoms" ("Sha1", "GameRomCatalogId", "Name")
                SELECT DISTINCT ON (item."Sha1")
                    item."Sha1",
                    catalog."Id",
                    item."Name"
                FROM
                    "GameRomCatalogs" AS catalog
                    CROSS JOIN ROWS FROM (jsonb_to_recordset(@Roms::jsonb) AS ("Sha1" text, "Name" text)) WITH ORDINALITY AS item ("Sha1", "Name", "Position")
                WHERE
                    catalog."Name" = @Name
                ORDER BY
                    item."Sha1",
                    item."Position";
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task SaveStoredFileHashAsync(this LogicContext ctx, StoredFileHashData data, CancellationToken ct)
        {
            // Ignore hashes of files changed meanwhile.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "StoredFiles" AS file
                SET
                    "ProbedAt" = @ProbedAt,
                    "Sha1" = @Sha1
                WHERE
                    file."Id" = @Id
                    AND file."SizeBytes" = @SizeBytes
                    AND file."ModifiedAt" = @ModifiedAt;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static Task<IReadOnlyList<GameRomFileData>> ReadGameRomFilesAsync(this LogicContext ctx, StoredFileCursorData data, CancellationToken ct)
        {
            // Unassigned files a DAT names.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT DISTINCT ON (file."Id")
                    file."Id" AS "StoredFileId",
                    rom."Name" AS "RomName"
                FROM
                    "StoredFiles" AS file
                    JOIN "GameRoms" AS rom ON rom."Sha1" = file."Sha1"
                WHERE
                    file."LibraryRootId" = @LibraryRootId
                    AND file."Id" > @AfterId
                    AND file."MediaAssetId" IS NULL
                    AND file."MissingSince" IS NULL
                ORDER BY
                    file."Id",
                    rom."GameRomCatalogId"
                LIMIT 100;
                """;
            sql.Parameters.Add(data);
            return ctx.ReadSql.ReadListAsync<GameRomFileData>(sql, ct);
        }

        public static Task<IReadOnlyList<WorkTitleMatchData>> ReadGameTitleMatchesAsync(this LogicContext ctx, WorkTitleLookupData data, CancellationToken ct)
        {
            // Titles compare like GameRomMatching.Key; only a single game wins.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    lookup."Key" AS "Title",
                    work."MediaTypeId",
                    min(work."Id") AS "WorkId"
                FROM
                    "Works" AS work
                    JOIN "WorkTitles" AS title ON title."WorkId" = work."Id"
                    JOIN unnest(@Titles) AS lookup ("Key") ON lookup."Key" = regexp_replace(lower(title."Title"), '[^[:alnum:]]+', '', 'g')
                WHERE
                    work."MediaTypeId" = 7
                GROUP BY
                    lookup."Key",
                    work."MediaTypeId"
                HAVING
                    count(DISTINCT work."Id") = 1;
                """;
            sql.Parameters.Add(data);
            return ctx.ReadSql.ReadListAsync<WorkTitleMatchData>(sql, ct);
        }

        public static async Task TagGameRomReleasesAsync(this LogicContext ctx, LibraryRootIdData data, CancellationToken ct)
        {
            // Files assigned before their hash was known still learn their platform.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "GameReleases" ("WorkVersionId", "GamePlatformId")
                SELECT DISTINCT ON (asset."WorkVersionId")
                    asset."WorkVersionId",
                    catalog."GamePlatformId"
                FROM
                    "StoredFiles" AS file
                    JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                    JOIN "GameRoms" AS rom ON rom."Sha1" = file."Sha1"
                    JOIN "GameRomCatalogs" AS catalog ON catalog."Id" = rom."GameRomCatalogId"
                WHERE
                    file."LibraryRootId" = @Id
                ORDER BY
                    asset."WorkVersionId",
                    catalog."Id"
                ON CONFLICT ("WorkVersionId") DO NOTHING;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
