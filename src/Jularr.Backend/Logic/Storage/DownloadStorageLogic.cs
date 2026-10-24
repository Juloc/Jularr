using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Logic.Storage;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Storage
{
    internal static class DownloadStorageLogic
    {
        // Configured means the Owner saved both native workspaces and a reserve.
        // Runtime mount identity and free-space checks remain in DownloadStorage.
        public static bool IsConfigured(DownloadStorageSettingsData settings) =>
            settings.IncompleteStorageMountId is not null
            && settings.CompleteStorageMountId is not null
            && settings.IncompletePath is not null
            && settings.CompletePath is not null
            && settings.IncompleteFilesystemIdentity is not null
            && settings.CompleteFilesystemIdentity is not null
            && settings.FreeSpaceReserveBytes is not null;

        public static async Task RequireDownloadStorageSettingsAsync(this LogicContext ctx, DownloadStorageSettingsData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    settings."Version"
                FROM
                    "DownloadStorageSettings" AS settings
                WHERE
                    settings."DownloadStorageAreaId" = @DownloadStorageAreaId
                FOR UPDATE;
                """;
            sql.Parameters.Add(data);
            var version = await ctx.ReadRequiredAsync<StorageMountVersionData>(sql, ct);
            if (version.Version != data.Version) throw new DataValidationException(nameof(data.Version), "outdated_version");
        }

        public static Task<DownloadStorageSettingsData> ReadDownloadStorageSettingsAsync(this LogicContext ctx, DownloadStorageArea area, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    settings."DownloadStorageAreaId",
                    settings."WorkspaceId",
                    settings."IncompleteStorageMountId",
                    settings."IncompletePath",
                    settings."IncompleteFilesystemIdentity",
                    settings."CompleteStorageMountId",
                    settings."CompletePath",
                    settings."CompleteFilesystemIdentity",
                    settings."FreeSpaceReserveBytes",
                    settings."MaxJobBytes",
                    settings."MaxWorkspaceBytes",
                    settings."MaxParallelJobs",
                    settings."Version"
                FROM
                    "DownloadStorageSettings" AS settings
                WHERE
                    settings."DownloadStorageAreaId" = @DownloadStorageAreaId;
                """;
            sql.Parameters.Add(new DownloadStorageAreaData(area));
            return ctx.ReadSql.ReadRequiredAsync<DownloadStorageSettingsData>(sql, ct);
        }

        public static async Task SaveDownloadStorageSettingsAsync(this LogicContext ctx, DownloadStorageSettingsData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "DownloadStorageSettings" AS settings
                SET
                    "IncompleteStorageMountId" = @IncompleteStorageMountId,
                    "IncompletePath" = @IncompletePath,
                    "IncompleteFilesystemIdentity" = @IncompleteFilesystemIdentity,
                    "CompleteStorageMountId" = @CompleteStorageMountId,
                    "CompletePath" = @CompletePath,
                    "CompleteFilesystemIdentity" = @CompleteFilesystemIdentity,
                    "FreeSpaceReserveBytes" = @FreeSpaceReserveBytes,
                    "MaxJobBytes" = @MaxJobBytes,
                    "MaxWorkspaceBytes" = @MaxWorkspaceBytes,
                    "MaxParallelJobs" = @MaxParallelJobs,
                    "Version" = settings."Version" + 1
                WHERE
                    settings."DownloadStorageAreaId" = @DownloadStorageAreaId
                    AND settings."Version" = @Version
                    AND (
                        (settings."IncompleteStorageMountId", settings."IncompletePath", settings."IncompleteFilesystemIdentity",
                         settings."CompleteStorageMountId", settings."CompletePath", settings."CompleteFilesystemIdentity") IS NOT DISTINCT FROM
                        (@IncompleteStorageMountId, @IncompletePath, @IncompleteFilesystemIdentity, @CompleteStorageMountId, @CompletePath, @CompleteFilesystemIdentity)
                        OR NOT EXISTS (
                            SELECT
                                1
                            FROM
                                "Operations" AS operation
                                JOIN "AcquisitionConnections" AS connection ON connection."Id" = operation."AcquisitionConnectionId"
                            WHERE
                                operation."OperationTypeId" = 2
                                AND operation."OperationStatusTypeId" IN (1, 2)
                                AND CASE WHEN @DownloadStorageAreaId = 1 THEN connection."AcquisitionConnectionTypeId" = 6
                                    ELSE connection."AcquisitionConnectionTypeId" IN (3, 4, 5) END
                        )
                    );
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(data.Version), "in_use");
            }
        }
    }
}
