using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.Logic.Reading;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Profile;

namespace Jularr.Backend.Logic.Reading
{
    internal static class ReadingBookmarkLogic
    {
        // The same place twice keeps one bookmark; 200 per book keep every list complete.
        public static async Task<long> SaveReadingBookmarkAsync(this LogicContext ctx, ReadingBookmarkSaveData data, CancellationToken ct)
        {
            // One profile saves one bookmark at a time.
            await ctx.LockProfileAsync(data.ProfileId, ct);

            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id" AS "ProfileId"
                FROM
                    "Profiles" AS profile
                WHERE
                    profile."Id" = @ProfileId
                    AND (
                        (SELECT count(*) FROM "ReadingBookmarks" AS bookmark WHERE bookmark."ProfileId" = profile."Id" AND bookmark."MediaAssetId" = @MediaAssetId) < 200
                        OR EXISTS (
                            SELECT 1
                            FROM "ReadingBookmarks" AS same
                            WHERE same."ProfileId" = profile."Id" AND same."MediaAssetId" = @MediaAssetId AND same."SectionIndex" = @SectionIndex AND same."Block" = @Block
                        )
                    );
                """;
            sql.Parameters.Add(data);
            if (await ctx.ReadOptionalAsync<ProfileParameters>(sql, ct) is null)
            {
                throw new DataValidationException(nameof(data.Block), "bookmark_limit");
            }

            // Only books the profile may see, text or images.
            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "ReadingBookmarks" ("ProfileId", "MediaAssetId", "SectionIndex", "Block", "Progression", "Title")
                SELECT
                    @ProfileId,
                    asset."Id",
                    @SectionIndex,
                    @Block,
                    @Progression,
                    @Title
                FROM
                    "MediaAssets" AS asset
                WHERE
                    asset."Id" = @MediaAssetId
                    AND asset."MediaAssetTypeId" IN (3, 4)
                    AND EXISTS (SELECT 1 FROM "ProfileWorks" AS visible WHERE visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId)
                ON CONFLICT ("ProfileId", "MediaAssetId", "SectionIndex", "Block") DO UPDATE
                SET
                    "Progression" = EXCLUDED."Progression",
                    "Title" = EXCLUDED."Title"
                RETURNING
                    "Id";
                """;
            sql.Parameters.Add(data);
            return (await ctx.ReadOptionalAsync<ReadingBookmarkIdData>(sql, ct) ?? throw new KeyNotFoundException("The book was not found.")).Id;
        }

        public static async Task DeleteReadingBookmarkAsync(this LogicContext ctx, ReadingBookmarkKeyData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "ReadingBookmarks" AS bookmark
                WHERE
                    bookmark."Id" = @ReadingBookmarkId
                    AND bookmark."ProfileId" = @ProfileId
                    AND bookmark."MediaAssetId" = @MediaAssetId;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new KeyNotFoundException("The bookmark was not found.");
            }
        }
    }
}
