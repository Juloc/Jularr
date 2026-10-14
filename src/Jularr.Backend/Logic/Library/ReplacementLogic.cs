using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Storage;
using Jularr.Backend.Infrastructure.Sql;

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
                    settings."RecyclesInLibraries",
                    settings."RecycleStorageMountId",
                    mount."StorageProtocolTypeId",
                    mount."SharePath",
                    settings."RecyclePath",
                    settings."RecycleDays"
                FROM
                    "InstanceSettings" AS settings
                    LEFT JOIN "StorageMounts" AS mount ON mount."Id" = settings."RecycleStorageMountId"
                WHERE
                    settings."Id" = 1;
                """;
            return ctx.ReadSql.ReadRequiredAsync<ReplacementSettingsData>(sql, ct);
        }

        public static async Task SaveReplacementSettingsAsync(this LogicContext ctx, ReplacementSettingsSaveData data, CancellationToken ct)
        {
            // Several releases of one film or episode need names that tell them apart.
            if (data.KeepsReleases && (await ctx.ReadNamingPatternsAsync(ct)).Values.Any(pattern => !NamingPattern.Distinguishes(pattern)))
            {
                throw new DataValidationException(nameof(data.KeepsReleases), "naming_needs_release_token");
            }

            if ((data.RecycleStorageMountId is null) != (data.RecyclePath is null))
            {
                throw new DataValidationException(nameof(data.RecyclePath), "required");
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

            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "InstanceSettings" AS settings
                SET
                    "KeepsReleases" = @KeepsReleases,
                    "RecyclesInLibraries" = @RecyclesInLibraries,
                    "RecycleStorageMountId" = @RecycleStorageMountId,
                    "RecyclePath" = @RecyclePath,
                    "RecycleDays" = @RecycleDays,
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
        }

        public static Task<IReadOnlyList<ReplacedFileData>> ReadReplacedFilesAsync(this LogicContext ctx, ReplacingAssetsData data, CancellationToken ct)
        {
            // Files of other assets of the same kind for the same Work and unit; games keep one asset per platform.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    replacing."Id" AS "NewMediaAssetId",
                    file."Id" AS "StoredFileId",
                    mount."Id" AS "StorageMountId",
                    mount."StorageProtocolTypeId",
                    mount."SharePath",
                    root."RelativePath" AS "RootPath",
                    file."RelativePath"
                FROM
                    "MediaAssets" AS replacing
                    JOIN "MediaAssets" AS earlier ON earlier."WorkId" = replacing."WorkId"
                        AND earlier."WorkUnitId" IS NOT DISTINCT FROM replacing."WorkUnitId"
                        AND earlier."MediaAssetTypeId" = replacing."MediaAssetTypeId"
                        AND earlier."Id" <> ALL (@MediaAssetIds)
                    JOIN "StoredFiles" AS file ON file."MediaAssetId" = earlier."Id"
                    JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                    JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                WHERE
                    replacing."Id" = ANY (@MediaAssetIds)
                    AND replacing."MediaAssetTypeId" <> 5
                    AND file."MissingSince" IS NULL
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
                WITH retired AS (
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

        // Libraries whose recycle folder may hold files.
        public static Task<IReadOnlyList<RecycleLibraryData>> ReadRecycleLibrariesAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    mount."Id" AS "StorageMountId",
                    mount."StorageProtocolTypeId",
                    mount."SharePath",
                    root."RelativePath" AS "RootPath"
                FROM
                    "LibraryRoots" AS root
                    JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                WHERE
                    NOT mount."IsReadOnly"
                ORDER BY
                    root."Id";
                """;
            return ctx.ReadSql.ReadListAsync<RecycleLibraryData>(sql, ct);
        }
    }
}
