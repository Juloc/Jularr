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
                    connection."Username",
                    connection."MaxConnections",
                    connection."Capabilities"::text AS "Capabilities",
                    connection."Version",
                    connection."ProtectedPassword"
                FROM
                    "AcquisitionConnections" AS connection
                WHERE
                    connection."Id" = @AcquisitionConnectionId;
                """;
            sql.Parameters.Add(new AcquisitionConnectionParameters(acquisitionConnectionId));
            return ctx.ReadSql.ReadRequiredAsync<AcquisitionConnectionData>(sql, ct);
        }

        // The enabled connections of one role, best priority first.
        public static Task<IReadOnlyList<AcquisitionConnectionData>> ReadEnabledConnectionsAsync(this LogicContext ctx, AcquisitionConnectionRole role, CancellationToken ct)
        {
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
                    connection."Username",
                    connection."MaxConnections",
                    connection."Capabilities"::text AS "Capabilities",
                    connection."Version",
                    connection."ProtectedPassword"
                FROM
                    "AcquisitionConnections" AS connection
                    JOIN "AcquisitionConnectionTypes" AS type ON type."Id" = connection."AcquisitionConnectionTypeId"
                WHERE
                    connection."IsEnabled"
                    AND type."AcquisitionConnectionRoleId" = @AcquisitionConnectionRoleId
                ORDER BY
                    connection."Priority",
                    connection."Id";
                """;
            sql.Parameters.Add(new AcquisitionConnectionRoleParameters(role));
            return ctx.ReadSql.ReadListAsync<AcquisitionConnectionData>(sql, ct);
        }

        // News servers are reached over NNTP, everything else over HTTP; the Anna's Archive member key travels in the address and only over HTTPS.
        private static void ValidateAddress(AcquisitionConnectionSaveData data)
        {
            var address = new Uri(data.BaseUrl);
            var valid = data.AcquisitionConnectionTypeId switch
            {
                AcquisitionConnectionType.Usenet => address.Scheme is "nntp" or "nntps" && address.AbsolutePath == "/",
                AcquisitionConnectionType.AnnasArchive => address.Scheme == "https",
                _ => address.Scheme is "http" or "https"
            };
            if (!valid)
            {
                throw new DataValidationException(nameof(data.BaseUrl), "invalid_url");
            }
        }

        public static async Task<long> CreateAcquisitionConnectionAsync(this LogicContext ctx, AcquisitionConnectionSaveData data, CancellationToken ct)
        {
            ValidateAddress(data);

            // Store protected configuration.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "AcquisitionConnections" ("Name", "AcquisitionConnectionTypeId", "BaseUrl", "ProtectedApiKey", "IsEnabled", "Priority", "Categories", "DownloadCategory",
                        "RemotePath", "LocalPath", "Username", "MaxConnections", "ProtectedPassword")
                VALUES
                    (@Name, @AcquisitionConnectionTypeId, @BaseUrl, @ProtectedApiKey, @IsEnabled, @Priority, @Categories, @DownloadCategory, @RemotePath, @LocalPath, @Username,
                        @MaxConnections, @ProtectedPassword)
                RETURNING
                    "Id";
                """;
            sql.Parameters.Add(data);
            return (await ctx.ReadRequiredAsync<AcquisitionConnectionIdData>(sql, ct)).Id;
        }

        public static async Task UpdateAcquisitionConnectionAsync(this LogicContext ctx, AcquisitionConnectionSaveData data, CancellationToken ct)
        {
            ValidateAddress(data);

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
                    "Username" = @Username,
                    "MaxConnections" = @MaxConnections,
                    "ProtectedPassword" = CASE WHEN @AcquisitionConnectionTypeId = 7 THEN COALESCE(@ProtectedPassword, connection."ProtectedPassword") END,
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
