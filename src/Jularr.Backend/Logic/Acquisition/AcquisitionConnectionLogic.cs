using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Acquisition
{
    internal static class AcquisitionConnectionLogic
    {
        public static Task<AcquisitionConnectionData> ReadAcquisitionConnectionAsync(this LogicContext ctx, long acquisitionConnectionId, CancellationToken ct)
        {
            // Connection facts for external work.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    connection."Id",
                    connection."Name",
                    connection."AcquisitionConnectionTypeId",
                    connection."BaseUrl",
                    connection."ProtectedApiKey",
                    connection."IsEnabled",
                    connection."Priority",
                    connection."Categories",
                    connection."DownloadCategory",
                    connection."RemotePath",
                    connection."LocalPath",
                    connection."Capabilities"::text AS "Capabilities",
                    connection."Version"
                FROM
                    "AcquisitionConnections" AS connection
                WHERE
                    connection."Id" = @AcquisitionConnectionId;
                """;
            sql.Parameters.Add(new AcquisitionConnectionParameters(acquisitionConnectionId));
            return ctx.ReadSql.ReadRequiredAsync<AcquisitionConnectionData>(sql, ct);
        }

        public static Task<IReadOnlyList<AcquisitionConnectionData>> ReadEnabledAcquisitionConnectionsAsync(this LogicContext ctx, AcquisitionConnectionType type, CancellationToken ct)
        {
            // Best priority first.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    connection."Id",
                    connection."Name",
                    connection."AcquisitionConnectionTypeId",
                    connection."BaseUrl",
                    connection."ProtectedApiKey",
                    connection."IsEnabled",
                    connection."Priority",
                    connection."Categories",
                    connection."DownloadCategory",
                    connection."RemotePath",
                    connection."LocalPath",
                    connection."Capabilities"::text AS "Capabilities",
                    connection."Version"
                FROM
                    "AcquisitionConnections" AS connection
                WHERE
                    connection."AcquisitionConnectionTypeId" = @AcquisitionConnectionTypeId
                    AND connection."IsEnabled"
                ORDER BY
                    connection."Priority",
                    connection."Id";
                """;
            sql.Parameters.Add(new AcquisitionConnectionTypeParameters(type));
            return ctx.ReadSql.ReadListAsync<AcquisitionConnectionData>(sql, ct);
        }

        public static Task<IReadOnlyList<AcquisitionConnectionData>> ReadEnabledSearchConnectionsAsync(this LogicContext ctx, CancellationToken ct)
        {
            // Every enabled source that searches: indexers and catalogs.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    connection."Id",
                    connection."Name",
                    connection."AcquisitionConnectionTypeId",
                    connection."BaseUrl",
                    connection."ProtectedApiKey",
                    connection."IsEnabled",
                    connection."Priority",
                    connection."Categories",
                    connection."DownloadCategory",
                    connection."RemotePath",
                    connection."LocalPath",
                    connection."Capabilities"::text AS "Capabilities",
                    connection."Version"
                FROM
                    "AcquisitionConnections" AS connection
                WHERE
                    connection."AcquisitionConnectionTypeId" IN (1, 3)
                    AND connection."IsEnabled"
                ORDER BY
                    connection."Priority",
                    connection."Id";
                """;
            return ctx.ReadSql.ReadListAsync<AcquisitionConnectionData>(sql, ct);
        }

        public static async Task<long> CreateAcquisitionConnectionAsync(this LogicContext ctx, AcquisitionConnectionSaveData data, CancellationToken ct)
        {
            // Store protected configuration.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "AcquisitionConnections" ("Name", "AcquisitionConnectionTypeId", "BaseUrl", "ProtectedApiKey", "IsEnabled", "Priority", "Categories", "DownloadCategory",
                        "RemotePath", "LocalPath")
                VALUES
                    (@Name, @AcquisitionConnectionTypeId, @BaseUrl, @ProtectedApiKey, @IsEnabled, @Priority, @Categories, @DownloadCategory, @RemotePath, @LocalPath)
                RETURNING
                    "Id";
                """;
            sql.Parameters.Add(data);
            return (await ctx.ReadRequiredAsync<AcquisitionConnectionIdData>(sql, ct)).Id;
        }

        public static async Task UpdateAcquisitionConnectionAsync(this LogicContext ctx, AcquisitionConnectionSaveData data, CancellationToken ct)
        {
            // Preserve an omitted API key; a new address or type needs a new check.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AcquisitionConnections" AS connection
                SET
                    "Name" = @Name,
                    "AcquisitionConnectionTypeId" = @AcquisitionConnectionTypeId,
                    "BaseUrl" = @BaseUrl,
                    "ProtectedApiKey" = COALESCE(@ProtectedApiKey, connection."ProtectedApiKey"),
                    "IsEnabled" = @IsEnabled,
                    "Priority" = @Priority,
                    "Categories" = @Categories,
                    "DownloadCategory" = @DownloadCategory,
                    "RemotePath" = @RemotePath,
                    "LocalPath" = @LocalPath,
                    "Capabilities" = CASE
                        WHEN connection."BaseUrl" = @BaseUrl AND connection."AcquisitionConnectionTypeId" = @AcquisitionConnectionTypeId AND @ProtectedApiKey IS NULL
                            THEN connection."Capabilities"
                    END,
                    "Version" = connection."Version" + 1,
                    "UpdatedAt" = statement_timestamp()
                WHERE
                    connection."Id" = @Id;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new KeyNotFoundException("The acquisition connection was not found.");
            }
        }

        public static async Task SaveAcquisitionConnectionCheckAsync(this LogicContext ctx, AcquisitionConnectionCheckData data, CancellationToken ct)
        {
            // A failed check keeps the last known capabilities; an edited connection ignores the old result.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AcquisitionConnections" AS connection
                SET
                    "Capabilities" = COALESCE(@Capabilities::jsonb, connection."Capabilities"),
                    "CheckedAt" = statement_timestamp(),
                    "LastErrorKey" = @ErrorKey
                WHERE
                    connection."Id" = @Id
                    AND connection."Version" = @Version;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task DeleteAcquisitionConnectionAsync(this LogicContext ctx, AcquisitionConnectionParameters data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    connection."Version"
                FROM
                    "AcquisitionConnections" AS connection
                WHERE
                    connection."Id" = @AcquisitionConnectionId
                FOR UPDATE;
                """;
            sql.Parameters.Add(data);
            var current = await ctx.ReadOptionalAsync<AcquisitionConnectionVersionData>(sql, ct) ?? throw new KeyNotFoundException("The acquisition connection was not found.");
            if (current.Version != data.Version)
            {
                throw new DataValidationException(nameof(AcquisitionConnectionParameters.Version), "outdated_version");
            }

            // Running downloads still need their client.
            sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "AcquisitionConnections" AS connection
                WHERE
                    connection."Id" = @AcquisitionConnectionId
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "Operations" AS operation
                        WHERE
                            operation."AcquisitionConnectionId" = connection."Id"
                            AND operation."OperationStatusTypeId" IN (1, 2)
                    );
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(AcquisitionConnectionParameters.AcquisitionConnectionId), "in_use");
            }
        }
    }
}
