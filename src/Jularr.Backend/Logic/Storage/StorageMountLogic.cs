using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Storage;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Storage
{
    internal static class StorageMountLogic
    {
        public static Task<StorageMountData> ReadStorageMountAsync(this LogicContext ctx, long storageMountId, CancellationToken ct)
        {
            // Connection facts for external work.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    mount."Id",
                    mount."Name",
                    mount."StorageProtocolTypeId",
                    mount."Host",
                    mount."SharePath",
                    mount."ProtocolVersion",
                    mount."Username",
                    mount."Domain",
                    mount."IsReadOnly",
                    mount."ProtectedPassword",
                    mount."WakeMacAddress",
                    mount."WakeBroadcastAddress",
                    mount."WakeOnDemand",
                    mount."Version"
                FROM
                    "StorageMounts" AS mount
                WHERE
                    mount."Id" = @StorageMountId;
                """;
            sql.Parameters.Add(new StorageMountParameters(storageMountId));
            return ctx.ReadSql.ReadRequiredAsync<StorageMountData>(sql, ct);
        }

        public static async Task<long> CreateStorageMountAsync(this LogicContext ctx, StorageMountSaveData data, CancellationToken ct)
        {
            // Store protected configuration.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "StorageMounts" ("Name", "StorageProtocolTypeId", "Host", "SharePath", "ProtocolVersion", "Username", "Domain", "ProtectedPassword", "IsReadOnly",
                        "WakeMacAddress", "WakeBroadcastAddress", "WakeOnDemand")
                VALUES
                    (@Name, @StorageProtocolTypeId, @Host, @SharePath, @ProtocolVersion, @Username, @Domain, @ProtectedPassword, @IsReadOnly,
                        @WakeMacAddress, @WakeBroadcastAddress, @WakeOnDemand)
                RETURNING
                    "Id";
                """;
            sql.Parameters.Add(data);
            return (await ctx.ReadRequiredAsync<StorageMountIdData>(sql, ct)).Id;
        }

        public static async Task UpdateStorageMountAsync(this LogicContext ctx, StorageMountSaveData data, CancellationToken ct)
        {
            // Preserve omitted SMB credentials.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "StorageMounts" AS mount
                SET
                    "Name" = @Name,
                    "StorageProtocolTypeId" = @StorageProtocolTypeId,
                    "Host" = @Host,
                    "SharePath" = @SharePath,
                    "ProtocolVersion" = @ProtocolVersion,
                    "Username" = @Username,
                    "Domain" = @Domain,
                    "ProtectedPassword" = CASE WHEN @StorageProtocolTypeId = 1 THEN COALESCE(@ProtectedPassword, mount."ProtectedPassword") END,
                    "IsReadOnly" = @IsReadOnly,
                    "WakeMacAddress" = @WakeMacAddress,
                    "WakeBroadcastAddress" = @WakeBroadcastAddress,
                    "WakeOnDemand" = @WakeOnDemand,
                    "Version" = mount."Version" + 1,
                    "UpdatedAt" = statement_timestamp()
                WHERE
                    mount."Id" = @Id;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new KeyNotFoundException("The storage connection was not found.");
            }
        }

        public static async Task DeleteStorageMountAsync(this LogicContext ctx, StorageMountParameters data, CancellationToken ct)
        {
            // Libraries keep their connection.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    mount."Version"
                FROM
                    "StorageMounts" AS mount
                WHERE
                    mount."Id" = @StorageMountId
                FOR UPDATE;
                """;
            sql.Parameters.Add(data);
            var current = await ctx.ReadOptionalAsync<StorageMountVersionData>(sql, ct) ?? throw new KeyNotFoundException("The storage connection was not found.");
            if (current.Version != data.Version)
            {
                throw new DataValidationException(nameof(StorageMountParameters.Version), "outdated_version");
            }

            sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "StorageMounts" AS mount
                WHERE
                    mount."Id" = @StorageMountId
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "LibraryRoots" AS root
                        WHERE
                            root."StorageMountId" = mount."Id"
                    );
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new DataValidationException(nameof(StorageMountParameters.StorageMountId), "in_use");
            }
        }
    }
}
