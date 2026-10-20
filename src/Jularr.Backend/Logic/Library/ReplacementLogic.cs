using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Storage;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;

namespace Jularr.Backend.Logic.Library
{
    // An import replaces the earlier file of a film, episode, track or volume, as Sonarr does, unless several releases are kept.
    internal static class ReplacementLogic
    {
        public static Task<ReplacementSettingsData> ReadReplacementSettingsAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    settings."KeepsReleases",
                    settings."RecycleModeId",
                    settings."RecycleStorageMountId",
                    mount."IsReadOnly",
                    settings."RecyclePath",
                    settings."RecycleDays",
                    settings."UnmonitorsDeletedUnits"
                FROM
                    "InstanceSettings" AS settings
                    LEFT JOIN "StorageMounts" AS mount ON mount."Id" = settings."RecycleStorageMountId"
                WHERE
                    settings."Id" = 1
                FOR SHARE OF
                    settings;
                """;
            return ctx.ReadRequiredAsync<ReplacementSettingsData>(sql, ct);
        }

        public static async Task SaveReplacementSettingsAsync(this LogicContext ctx, ReplacementSettingsSaveData input, CancellationToken ct)
        {
            if (input.RecycleModeId == RecycleMode.OwnFolder && (input.RecycleStorageMountId is null || input.RecyclePath is null))
            {
                throw new DataValidationException(nameof(input.RecyclePath), "required");
            }

            // Only the own folder names a place; the other modes forget it.
            var data = input.RecycleModeId == RecycleMode.OwnFolder ? input
                : new ReplacementSettingsSaveData(input.KeepsReleases, input.RecycleModeId, null, null, input.RecycleDays, input.Version, input.UnmonitorsDeletedUnits);

            // The settings row stays locked, so a pattern edit waits and sees the new choice.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "InstanceSettings" AS settings
                SET
                    "KeepsReleases" = @KeepsReleases,
                    "RecycleModeId" = @RecycleModeId,
                    "RecycleStorageMountId" = @RecycleStorageMountId,
                    "RecyclePath" = @RecyclePath,
                    "RecycleDays" = @RecycleDays,
                    "UnmonitorsDeletedUnits" = @UnmonitorsDeletedUnits,
                    "Version" = settings."Version" + 1
                WHERE
                    settings."Id" = 1
                    AND settings."Version" = @Version;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.Version), "invalid_transition");
            }

            // Several releases of one film or episode need names that tell them apart.
            if (data.KeepsReleases && (await ctx.ReadNamingPatternsAsync(ct)).Patterns.Any(pattern => !NamingPattern.Distinguishes(pattern)))
            {
                throw new DataValidationException(nameof(data.KeepsReleases), "naming_needs_release_token");
            }

            // The folder lies on a writable connection, outside every library, so no scan takes it for media.
            if (data.RecycleStorageMountId is not null)
            {
                var folder = new SqlStatement();
                folder.Statement =
                    """
                    SELECT
                        mount."Id"
                    FROM
                        "StorageMounts" AS mount
                    WHERE
                        mount."Id" = @RecycleStorageMountId
                        AND NOT mount."IsReadOnly"
                        AND NOT EXISTS (
                            SELECT
                                1
                            FROM
                                "LibraryRoots" AS root
                            WHERE
                                root."StorageMountId" = mount."Id"
                                AND (
                                    root."RelativePath" = @RecyclePath
                                    OR starts_with(@RecyclePath, root."RelativePath" || '/')
                                    OR starts_with(root."RelativePath", @RecyclePath || '/')
                                    OR root."RelativePath" = ''
                                )
                        );
                    """;
                folder.Parameters.Add(data);
                _ = await ctx.ReadOptionalAsync<StorageMountIdData>(folder, ct) ?? throw new DataValidationException(nameof(data.RecyclePath), "invalid_value");
            }
        }

        public static Task<IReadOnlyList<ReplacedFileData>> ReadReplacedFilesAsync(this LogicContext ctx, ReplacingAssetsData data, CancellationToken ct)
        {
            // Files of the other versions on writable libraries; protected versions only go on an administrator's confirmation.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    replacing."Id" AS "NewMediaAssetId",
                    file."Id" AS "StoredFileId",
                    mount."Id" AS "StorageMountId",
                    root."RelativePath" AS "RootPath",
                    file."RelativePath",
                    earlierMedia."QualityKey" AS "EarlierQualityKey",
                    earlierMedia."Revision" AS "EarlierRevision",
                    replacingMedia."QualityKey" AS "NewQualityKey",
                    replacingMedia."Revision" AS "NewRevision",
                    earlierFacts."Evidence"::text AS "EarlierEvidence",
                    newFacts."Evidence"::text AS "NewEvidence"
                FROM
                    "MediaAssets" AS replacing
                    JOIN "MediaAssetSiblings" AS sibling ON sibling."MediaAssetId" = replacing."Id"
                    JOIN "MediaAssets" AS earlier ON earlier."Id" = sibling."SiblingMediaAssetId" AND earlier."Id" <> ALL (@MediaAssetIds)
                    JOIN LATERAL (
                        SELECT
                            main."Id",
                            main."QualityKey",
                            main."Revision"
                        FROM
                            "StoredFiles" AS main
                        WHERE
                            main."MediaAssetId" = earlier."Id"
                            AND NOT main."IsSubtitle"
                        ORDER BY
                            main."Id"
                        LIMIT 1
                    ) AS earlierMedia ON TRUE
                    JOIN LATERAL (
                        SELECT
                            main."Id",
                            main."QualityKey",
                            main."Revision"
                        FROM
                            "StoredFiles" AS main
                        WHERE
                            main."MediaAssetId" = replacing."Id"
                            AND NOT main."IsSubtitle"
                        ORDER BY
                            main."Id"
                        LIMIT 1
                    ) AS replacingMedia ON TRUE
                    LEFT JOIN "StoredFileFormatEvidence" AS earlierFacts ON earlierFacts."StoredFileId" = earlierMedia."Id"
                    LEFT JOIN "StoredFileFormatEvidence" AS newFacts ON newFacts."StoredFileId" = replacingMedia."Id"
                    JOIN "StoredFiles" AS file ON file."MediaAssetId" = earlier."Id"
                    JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                    JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                WHERE
                    replacing."Id" = ANY (@MediaAssetIds)
                    AND file."MissingSince" IS NULL
                    AND NOT mount."IsReadOnly"
                    AND (@IsConfirmed OR NOT earlier."IsProtected")
                ORDER BY
                    file."Id";
                """;
            sql.Parameters.Add(data);
            return ctx.ReadSql.ReadListAsync<ReplacedFileData>(sql, ct);
        }

        public static async Task RetireReplacedFilesAsync(this LogicContext ctx, RetiredFilesData data, CancellationToken ct)
        {
            // Playback history and notes follow the new asset; the files and assets left empty go.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH recycled AS (
                    INSERT INTO
                        "RecycledFiles" ("StorageMountId", "FolderPath", "RelativePath")
                    SELECT
                        item."StorageMountId",
                        item."FolderPath",
                        item."RecycledPath"
                    FROM
                        jsonb_to_recordset(@Files::jsonb) AS item("StorageMountId" bigint, "FolderPath" text, "RecycledPath" text)
                    WHERE
                        item."RecycledPath" IS NOT NULL
                    ON CONFLICT ("StorageMountId", "FolderPath", "RelativePath") DO UPDATE
                    SET
                        "RecycledAt" = now()
                ),
                retired AS (
                    SELECT
                        item."StoredFileId",
                        item."NewMediaAssetId",
                        file."MediaAssetId" AS "EarlierMediaAssetId",
                        (
                            SELECT
                                min(media."Id")
                            FROM
                                "StoredFiles" AS media
                            WHERE
                                media."MediaAssetId" = item."NewMediaAssetId"
                                AND NOT media."IsSubtitle"
                        ) AS "NewStoredFileId"
                    FROM
                        jsonb_to_recordset(@Files::jsonb) AS item("StoredFileId" bigint, "NewMediaAssetId" bigint)
                        JOIN "StoredFiles" AS file ON file."Id" = item."StoredFileId"
                ),
                sessions AS (
                    UPDATE
                        "PlaybackSessions" AS session
                    SET
                        "MediaAssetId" = retired."NewMediaAssetId",
                        "StoredFileId" = retired."NewStoredFileId"
                    FROM
                        retired
                    WHERE
                        session."StoredFileId" = retired."StoredFileId"
                        AND retired."NewStoredFileId" IS NOT NULL
                ),
                notes AS (
                    UPDATE
                        "ReadingAnnotations" AS note
                    SET
                        "MediaAssetId" = retired."NewMediaAssetId"
                    FROM
                        retired
                    WHERE
                        note."MediaAssetId" = retired."EarlierMediaAssetId"
                ),
                removed AS (
                    DELETE FROM
                        "StoredFiles" AS file
                    USING
                        retired
                    WHERE
                        file."Id" = retired."StoredFileId"
                        AND retired."NewStoredFileId" IS NOT NULL
                    RETURNING
                        retired."EarlierMediaAssetId"
                )
                DELETE FROM
                    "MediaAssets" AS asset
                WHERE
                    asset."Id" IN (
                        SELECT
                            removed."EarlierMediaAssetId"
                        FROM
                            removed
                    )
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "StoredFiles" AS remaining
                        WHERE
                            remaining."MediaAssetId" = asset."Id"
                            AND remaining."Id" NOT IN (
                                SELECT
                                    retired."StoredFileId"
                                FROM
                                    retired
                            )
                    );
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // An import compares the same quality, format and language evidence as selection; later revisions also replace equal versions.
        // When either quality is unknown to the profile, only the administrator decides.
        public static bool IsBetter(QualityProfile profile, ReplacedFileData file)
        {
            var earlier = UpgradePolicy.RankOf(profile, file.EarlierQualityKey);
            var replacing = UpgradePolicy.RankOf(profile, file.NewQualityKey);
            if (replacing == int.MaxValue || earlier == int.MaxValue) return false;
            if (replacing != earlier) return replacing < earlier;
            if (file.EarlierEvidence is { } earlierJson && file.NewEvidence is { } newJson)
            {
                var earlierFacts = JsonSerializer.Deserialize<StoredReleaseEvidence>(earlierJson)!;
                var newFacts = JsonSerializer.Deserialize<StoredReleaseEvidence>(newJson)!;
                if (StoredFileFormatLogic.Candidate(earlierFacts) is { } before && StoredFileFormatLogic.Candidate(newFacts) is { } after)
                {
                    var beforeScore = ReleaseScorer.Score(profile, before).FormatScore;
                    var afterScore = ReleaseScorer.Score(profile, after).FormatScore;
                    if (beforeScore != afterScore) return afterScore > beforeScore;
                    if (profile.IsBook && UpgradePolicy.LanguageRank(profile, before.Languages ?? []) != UpgradePolicy.LanguageRank(profile, after.Languages ?? []))
                    {
                        return UpgradePolicy.LanguageRank(profile, after.Languages ?? []) < UpgradePolicy.LanguageRank(profile, before.Languages ?? []);
                    }
                }
            }
            return file.NewRevision > file.EarlierRevision;
        }

        public static async Task ProtectStoredFileAsync(this LogicContext ctx, StoredFileProtectionData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "MediaAssets" AS asset
                SET
                    "IsProtected" = @IsProtected
                FROM
                    "StoredFiles" AS file
                WHERE
                    file."Id" = @StoredFileId
                    AND asset."Id" = file."MediaAssetId";
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new KeyNotFoundException("The file has no version to protect.");
            }
        }

        // Recycled files older than the configured days; none while they are kept.
        public static Task<IReadOnlyList<RecycledFileData>> ReadExpiredRecycledFilesAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    recycled."Id",
                    mount."Id" AS "StorageMountId",
                    recycled."FolderPath",
                    recycled."RelativePath"
                FROM
                    "RecycledFiles" AS recycled
                    JOIN "StorageMounts" AS mount ON mount."Id" = recycled."StorageMountId"
                    CROSS JOIN "InstanceSettings" AS settings
                WHERE
                    settings."Id" = 1
                    AND settings."RecycleDays" > 0
                    AND recycled."RecycledAt" < now() - make_interval(days => settings."RecycleDays")
                ORDER BY
                    recycled."RecycledAt";
                """;
            return ctx.ReadSql.ReadListAsync<RecycledFileData>(sql, ct);
        }

        public static async Task ForgetRecycledFilesAsync(this LogicContext ctx, RecycledFileIdsData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "RecycledFiles" AS recycled
                WHERE
                    recycled."Id" = ANY (@Ids);
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
