using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    internal static class ArtworkNasExportLogic
    {
        public static Task<IReadOnlyList<ArtworkExportLocationData>> ReadNasExportLocationsAsync(this LogicContext ctx, WorkParametersV1 parameters, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    root."Id" AS "LibraryRootId",
                    root."StorageMountId",
                    work."MediaTypeId",
                    root."RelativePath" AS "RootRelativePath",
                    file."RelativePath"
                FROM
                    "StoredFiles" AS file
                    JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                    JOIN "Works" AS work ON work."Id" = asset."WorkId"
                    JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                    JOIN "StorageMounts" AS mount ON mount."Id" = root."StorageMountId"
                WHERE
                    asset."WorkId" = @WorkId
                    AND file."MissingSince" IS NULL
                    AND NOT file."IsSubtitle"
                    AND NOT mount."IsReadOnly"
                ORDER BY
                    root."Id",
                    file."RelativePath"
                LIMIT 200;
                """;
            sql.Parameters.Add(parameters);
            return ctx.ReadSql.ReadListAsync<ArtworkExportLocationData>(sql, ct);
        }

        public static Task<ArtworkExclusiveFolderData> ReadExclusiveNasFolderAsync(this LogicContext ctx, ArtworkExportFolderData target, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    (
                        count(DISTINCT asset."WorkId") = 1
                        AND min(asset."WorkId") = @WorkId
                    ) AS "IsExclusive"
                FROM
                    "StoredFiles" AS file
                    JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                WHERE
                    file."LibraryRootId" = @LibraryRootId
                    AND file."MissingSince" IS NULL
                    AND left(file."RelativePath", length(@Folder) + 1) = @Folder || '/';
                """;
            sql.Parameters.Add(target);
            return ctx.ReadSql.ReadRequiredAsync<ArtworkExclusiveFolderData>(sql, ct);
        }

        public static Task<ArtworkNasExportSettingsData> ReadNasExportSettingsAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    "ExportToNas",
                    "NasModeId",
                    "ProgressiveJxl"
                FROM
                    "ArtworkSettings"
                WHERE
                    "Id" = 1;
                """;
            return ctx.ReadSql.ReadRequiredAsync<ArtworkNasExportSettingsData>(sql, ct);
        }

        public static Task<IReadOnlyList<ArtworkExportFactData>> ReadNasExportFactsAsync(this LogicContext ctx, WorkParametersV1 parameters, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    "FactKey",
                    "Value"
                FROM
                    "WorkMetadataFacts"
                WHERE
                    "WorkId" = @WorkId
                    AND "FactKey" IN ('Poster', 'Backdrop')
                ORDER BY
                    "IsManual" DESC,
                    "UpdatedAt" DESC,
                    "Id" DESC
                LIMIT 12;
                """;
            sql.Parameters.Add(parameters);
            return ctx.ReadSql.ReadListAsync<ArtworkExportFactData>(sql, ct);
        }
    }
}
