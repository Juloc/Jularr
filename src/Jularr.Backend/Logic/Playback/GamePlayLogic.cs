using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Playback
{
    // Saves and play time belong to the profile; only games of an emulated platform the profile sees are played.
    internal static class GamePlayLogic
    {
        public static async Task SaveGameAsync(this LogicContext ctx, GameSaveWriteData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "GameSaves" ("ProfileId", "MediaAssetId", "GameSaveTypeId", "Data")
                SELECT
                    @ProfileId,
                    asset."Id",
                    @GameSaveTypeId,
                    @Data
                FROM
                    "MediaAssets" AS asset
                    JOIN "GameReleases" AS release ON release."WorkVersionId" = asset."WorkVersionId"
                    JOIN "GamePlatforms" AS platform ON platform."Id" = release."GamePlatformId"
                WHERE
                    asset."Id" = @MediaAssetId
                    AND asset."MediaAssetTypeId" = 5
                    AND platform."IgdbId" = ANY(@EmulatedPlatforms)
                    AND EXISTS (SELECT 1 FROM "ProfileWorks" AS visible WHERE visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId)
                ON CONFLICT ("ProfileId", "MediaAssetId", "GameSaveTypeId") DO UPDATE
                SET
                    "Data" = EXCLUDED."Data",
                    "UpdatedAt" = now();
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new KeyNotFoundException("The game was not found.");
            }
        }

        // Play time adds up in the game's progress; each report covers at most a few minutes.
        public static async Task RecordGamePlayAsync(this LogicContext ctx, GamePlayData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "MediaProgress" ("ProfileId", "WorkId", "WorkUnitId", "ProgressPositionTypeId", "PositionMs")
                SELECT
                    @ProfileId,
                    asset."WorkId",
                    asset."WorkUnitId",
                    3,
                    @Seconds * 1000::bigint
                FROM
                    "MediaAssets" AS asset
                    JOIN "GameReleases" AS release ON release."WorkVersionId" = asset."WorkVersionId"
                    JOIN "GamePlatforms" AS platform ON platform."Id" = release."GamePlatformId"
                WHERE
                    asset."Id" = @MediaAssetId
                    AND asset."MediaAssetTypeId" = 5
                    AND platform."IgdbId" = ANY(@EmulatedPlatforms)
                    AND EXISTS (SELECT 1 FROM "ProfileWorks" AS visible WHERE visible."WorkId" = asset."WorkId" AND visible."ProfileId" = @ProfileId)
                ON CONFLICT ("ProfileId", "WorkId", "WorkUnitId") DO UPDATE
                SET
                    "ProgressPositionTypeId" = 3,
                    "PositionMs" = CASE WHEN "MediaProgress"."ProgressPositionTypeId" = 3 THEN coalesce("MediaProgress"."PositionMs", 0) ELSE 0 END + EXCLUDED."PositionMs",
                    "Locator" = NULL,
                    "Progression" = NULL,
                    "UpdatedAt" = now();
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new KeyNotFoundException("The game was not found.");
            }
        }
    }
}
