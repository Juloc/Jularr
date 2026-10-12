using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Library;

namespace Jularr.Backend.Logic.Media
{
    internal static class MediaAssetLogic
    {
        public static async Task<MediaAssetIdData> AssignStoredFileAsync(this LogicContext ctx, StoredFileAssignmentData data, CancellationToken ct)
        {
            // Only unassigned media files start an asset.
            var file = await ctx.LockStoredFileAsync(data.StoredFileId, ct);
            var assetType = LibraryFileTypes.GetAssetType(file.RelativePath);
            if (file.MediaAssetId is not null || assetType is null)
            {
                throw new DataValidationException(nameof(StoredFileAssignmentData.StoredFileId), "invalid_transition");
            }

            ValidateUnit(file.MediaTypeId, data);
            var workId = await ctx.ResolveWorkAsync(file.MediaTypeId, data, ct);

            // The default edition serializes assignments per Work; the upsert always returns it.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "WorkEditions" ("WorkId")
                VALUES
                    (@Id)
                ON CONFLICT ("WorkId", "Name") DO UPDATE
                SET
                    "WorkId" = EXCLUDED."WorkId"
                RETURNING
                    "Id";
                """;
            sql.Parameters.Add(new WorkIdData(workId));
            var editionId = (await ctx.ReadRequiredAsync<WorkIdData>(sql, ct)).Id;

            // Structure units are shared by all versions.
            long? unitId = null;
            if (data.WorkUnitTypeId is { } unitType)
            {
                long? parentId = null;
                if (data.ParentUnitNumber is not null && WorkUnitTypes.ParentOf(unitType) is { } type)
                {
                    parentId = await ctx.EnsureWorkUnitAsync(new WorkUnitData(workId, null, type, data.ParentUnitNumber!.Value), ct);
                }

                unitId = await ctx.EnsureWorkUnitAsync(new WorkUnitData(workId, parentId, unitType, data.UnitNumber!.Value), ct);
            }

            // Duplicates of a unit become further versions.
            sql = new SqlStatement();
            sql.Statement =
                """
                WITH available AS (
                    SELECT
                        version."Id"
                    FROM
                        "WorkVersions" AS version
                    WHERE
                        version."WorkEditionId" = @WorkEditionId
                        AND NOT EXISTS (
                            SELECT
                                1
                            FROM
                                "MediaAssets" AS asset
                            WHERE
                                asset."WorkVersionId" = version."Id"
                                AND asset."WorkUnitId" IS NOT DISTINCT FROM @WorkUnitId
                        )
                    ORDER BY
                        version."Id"
                    LIMIT 1
                ), created AS (
                    INSERT INTO
                        "WorkVersions" ("WorkId", "WorkEditionId")
                    SELECT
                        @WorkId,
                        @WorkEditionId
                    WHERE
                        NOT EXISTS (SELECT 1 FROM available)
                    RETURNING
                        "Id"
                )
                INSERT INTO
                    "MediaAssets" ("WorkId", "WorkVersionId", "WorkUnitId", "MediaAssetTypeId")
                SELECT
                    @WorkId,
                    COALESCE((SELECT available."Id" FROM available), (SELECT created."Id" FROM created)),
                    @WorkUnitId,
                    @MediaAssetTypeId
                RETURNING
                    "Id";
                """;
            sql.Parameters.Add(new MediaAssetCreationData(workId, editionId, unitId, assetType.Value));
            var asset = await ctx.ReadRequiredAsync<MediaAssetIdData>(sql, ct);

            // Subtitles beside the file follow it.
            sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "StoredFiles" AS file
                SET
                    "MediaAssetId" = @MediaAssetId
                WHERE
                    file."Id" = @StoredFileId
                    OR (file."LibraryRootId" = @LibraryRootId
                        AND file."MediaAssetId" IS NULL
                        AND left(file."RelativePath", length(@Stem) + 1) = @Stem || '.'
                        AND file."RelativePath" ~* '\.(srt|ass|ssa|vtt|sub|idx|sup)$');
                """;
            sql.Parameters.Add(new StoredFileLinkData(file.Id, file.LibraryRootId, Stem(file.RelativePath), asset.Id));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            return asset;
        }

        public static async Task UnassignStoredFileAsync(this LogicContext ctx, StoredFileParameters data, CancellationToken ct)
        {
            var file = await ctx.LockStoredFileAsync(data.StoredFileId, ct);
            if (file.MediaAssetId is null)
            {
                throw new DataValidationException(nameof(StoredFileParameters.StoredFileId), "invalid_transition");
            }

            // Release the file and its subtitles.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "StoredFiles" AS file
                SET
                    "MediaAssetId" = NULL
                WHERE
                    file."MediaAssetId" = @MediaAssetId
                    AND (file."Id" = @StoredFileId
                        OR (left(file."RelativePath", length(@Stem) + 1) = @Stem || '.'
                            AND file."RelativePath" ~* '\.(srt|ass|ssa|vtt|sub|idx|sup)$'));
                """;
            sql.Parameters.Add(new StoredFileLinkData(file.Id, file.LibraryRootId, Stem(file.RelativePath), file.MediaAssetId));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            // Assets without files carry no inventory.
            sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "MediaAssets" AS asset
                WHERE
                    asset."Id" = @MediaAssetId
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "StoredFiles" AS file
                        WHERE
                            file."MediaAssetId" = asset."Id"
                    );
                """;
            sql.Parameters.Add(new StoredFileLinkData(file.Id, file.LibraryRootId, Stem(file.RelativePath), file.MediaAssetId));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        private static void ValidateUnit(MediaType mediaType, StoredFileAssignmentData data)
        {
            // Unit kinds follow the media type.
            if (data.WorkUnitTypeId is null)
            {
                if (data.UnitNumber is not null || data.ParentUnitNumber is not null)
                {
                    throw new DataValidationException(nameof(StoredFileAssignmentData.WorkUnitTypeId), "required");
                }

                return;
            }

            var parent = WorkUnitTypes.ParentOf(data.WorkUnitTypeId.Value);
            var allowed = WorkUnitTypes.For(mediaType).Contains(data.WorkUnitTypeId.Value)
                && (data.ParentUnitNumber is null || parent is not null)
                && (data.WorkUnitTypeId != WorkUnitType.Episode || data.ParentUnitNumber is not null);
            if (!allowed)
            {
                throw new DataValidationException(nameof(StoredFileAssignmentData.WorkUnitTypeId), "unknown_catalog_value");
            }

            if (data.UnitNumber is null)
            {
                throw new DataValidationException(nameof(StoredFileAssignmentData.UnitNumber), "required");
            }
        }

        private static async Task<long> ResolveWorkAsync(this LogicContext ctx, MediaType mediaType, StoredFileAssignmentData data, CancellationToken ct)
        {
            // Exactly one Work source.
            if ((data.WorkId is null) == (data.Title is null))
            {
                throw new DataValidationException(nameof(StoredFileAssignmentData.WorkId), "required");
            }

            if (data.Title is { } title)
            {
                return (await ctx.CreateWorkAsync(new WorkCreationData(mediaType, title), ct)).Id;
            }

            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id"
                FROM
                    "Works" AS work
                WHERE
                    work."Id" = @WorkId
                    AND work."MediaTypeId" = @MediaTypeId
                FOR SHARE;
                """;
            sql.Parameters.Add(new WorkLookupData(data.WorkId!.Value, mediaType));
            return (await ctx.ReadOptionalAsync<WorkIdData>(sql, ct) ?? throw new DataValidationException(nameof(StoredFileAssignmentData.WorkId), "unknown_reference")).Id;
        }

        private static async Task<long> EnsureWorkUnitAsync(this LogicContext ctx, WorkUnitData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "WorkUnits" ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number")
                VALUES
                    (@WorkId, @ParentWorkUnitId, @WorkUnitTypeId, @Number)
                ON CONFLICT ("WorkId", "ParentWorkUnitId", "WorkUnitTypeId", "Number") DO UPDATE
                SET
                    "Number" = EXCLUDED."Number"
                RETURNING
                    "Id";
                """;
            sql.Parameters.Add(data);
            return (await ctx.ReadRequiredAsync<WorkIdData>(sql, ct)).Id;
        }

        private static Task<StoredFileLockData> LockStoredFileAsync(this LogicContext ctx, long storedFileId, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    file."Id",
                    file."LibraryRootId",
                    file."RelativePath",
                    file."MediaAssetId",
                    root."MediaTypeId"
                FROM
                    "StoredFiles" AS file
                    JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                WHERE
                    file."Id" = @StoredFileId
                FOR UPDATE OF file;
                """;
            sql.Parameters.Add(new StoredFileParameters(storedFileId));
            return ctx.ReadRequiredAsync<StoredFileLockData>(sql, ct);
        }

        private static string Stem(string relativePath) => relativePath[..^Path.GetExtension(relativePath).Length];
    }
}
