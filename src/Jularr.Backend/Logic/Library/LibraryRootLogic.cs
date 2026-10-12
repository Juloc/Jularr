using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Storage;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Library
{
    internal static class LibraryRootLogic
    {
        public static async Task<long> CreateLibraryRootAsync(this LogicContext ctx, LibraryRootCreationData data, CancellationToken ct)
        {
            // Serialize root changes per connection.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    mount."Id"
                FROM
                    "StorageMounts" AS mount
                WHERE
                    mount."Id" = @StorageMountId
                FOR UPDATE;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ReadOptionalAsync<StorageMountIdData>(sql, ct) is null)
            {
                throw new DataValidationException(nameof(LibraryRootCreationData.StorageMountId), "unknown_reference");
            }

            // Nested roots would scan files twice.
            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    root."Id"
                FROM
                    "LibraryRoots" AS root
                WHERE
                    root."StorageMountId" = @StorageMountId
                    AND (root."RelativePath" = ''
                        OR @RelativePath = ''
                        OR root."RelativePath" = @RelativePath
                        OR left(@RelativePath, length(root."RelativePath") + 1) = root."RelativePath" || '/'
                        OR left(root."RelativePath", length(@RelativePath) + 1) = @RelativePath || '/')
                LIMIT 1;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ReadOptionalAsync<LibraryRootIdData>(sql, ct) is not null)
            {
                throw new DataValidationException(nameof(LibraryRootCreationData.RelativePath), "overlapping_path");
            }

            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "LibraryRoots" ("Name", "MediaTypeId", "StorageMountId", "RelativePath")
                VALUES
                    (@Name, @MediaTypeId, @StorageMountId, @RelativePath)
                RETURNING
                    "Id";
                """;
            sql.Parameters.Add(data);
            return (await ctx.ReadRequiredAsync<LibraryRootIdData>(sql, ct)).Id;
        }

        public static async Task RenameLibraryRootAsync(this LogicContext ctx, LibraryRootRenameData data, CancellationToken ct)
        {
            // Location changes would orphan the inventory.
            await ctx.LockLibraryRootAsync(new LibraryRootParameters(data.LibraryRootId, data.Version), ct);
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "LibraryRoots" AS root
                SET
                    "Name" = @Name,
                    "Version" = root."Version" + 1,
                    "UpdatedAt" = now()
                WHERE
                    root."Id" = @LibraryRootId;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task DeleteLibraryRootAsync(this LogicContext ctx, LibraryRootParameters data, CancellationToken ct)
        {
            await ctx.LockLibraryRootAsync(data, ct);

            // Remove inventory and assets that lose their last file.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH removed AS (
                    DELETE FROM
                        "StoredFiles" AS file
                    WHERE
                        file."LibraryRootId" = @LibraryRootId
                    RETURNING
                        file."MediaAssetId"
                )
                DELETE FROM
                    "MediaAssets" AS asset
                WHERE
                    asset."Id" IN (SELECT removed."MediaAssetId" FROM removed)
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "StoredFiles" AS other
                        WHERE
                            other."MediaAssetId" = asset."Id"
                            AND other."LibraryRootId" <> @LibraryRootId
                    );
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "LibraryRoots" AS root
                WHERE
                    root."Id" = @LibraryRootId;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        private static async Task<LibraryRootVersionData> LockLibraryRootAsync(this LogicContext ctx, LibraryRootParameters data, CancellationToken ct)
        {
            // Reject stale edits.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    root."Version"
                FROM
                    "LibraryRoots" AS root
                WHERE
                    root."Id" = @LibraryRootId
                FOR UPDATE;
                """;
            sql.Parameters.Add(data);
            var current = await ctx.ReadOptionalAsync<LibraryRootVersionData>(sql, ct) ?? throw new KeyNotFoundException("The library root was not found.");
            if (current.Version != data.Version)
            {
                throw new DataValidationException(nameof(LibraryRootParameters.Version), "outdated_version");
            }

            return current;
        }
    }
}
