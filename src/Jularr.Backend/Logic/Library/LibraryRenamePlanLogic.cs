using System.Text.Json;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Library
{
    // The plan of all libraries is kept, so the Rename tab reads one page of it instead of planning every file on each visit.
    // The worker builds it again after scans, downloads, naming and assignment changes and an hour after the last build; applying always plans anew.
    internal static partial class LibraryRenameLogic
    {
        // The plan of all libraries as the files are now, and the database time the files were read at (the start of this transaction).
        public static async Task<LibraryRenamePlanBuild> PlanLibraryRenamesAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement = "SELECT now();";
            var now = (DateTime)(await ctx.ReadSql.ExecuteScalarAsync(sql, ct))!;
            var renames = Plan(await ctx.ReadLibraryRenameFilesAsync(null, ct), await ctx.ReadNamingPatternsAsync(ct));
            return new LibraryRenamePlanBuild(now, renames);
        }

        // Keeps a plan unless a newer one is kept already. A request made before its files were read is answered by it.
        public static async Task<bool> SaveLibraryRenamePlanAsync(this LogicContext ctx, LibraryRenamePlanBuild plan, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement = "SELECT pg_advisory_xact_lock(hashtextextended('library-rename-plan', 0));";
            await ctx.Sql.ExecuteScalarAsync(sql, ct);

            var items = plan.Renames.Select(rename => new PlanItem(rename.StoredFileId, rename.MediaStoredFileId, rename.LibraryRootId, rename.LibraryRootName, rename.RelativePath,
                rename.TargetPath)).ToArray();
            sql = new SqlStatement();
            sql.Statement =
                """
                WITH kept AS (
                    INSERT INTO "LibraryRenamePlans" ("Id", "Fingerprint", "RenameCount", "BuiltAt", "RequestedAt")
                    VALUES (1, @Fingerprint, @RenameCount, @BuiltAt, NULL)
                    ON CONFLICT ("Id") DO UPDATE
                    SET
                        "Fingerprint" = EXCLUDED."Fingerprint",
                        "RenameCount" = EXCLUDED."RenameCount",
                        "BuiltAt" = EXCLUDED."BuiltAt",
                        "RequestedAt" = CASE WHEN "LibraryRenamePlans"."RequestedAt" > EXCLUDED."BuiltAt" THEN "LibraryRenamePlans"."RequestedAt" END
                    WHERE
                        "LibraryRenamePlans"."BuiltAt" <= EXCLUDED."BuiltAt"
                    RETURNING
                        1
                ), removed AS (
                    DELETE FROM
                        "LibraryRenamePlanItems"
                    WHERE
                        EXISTS (SELECT 1 FROM kept)
                    RETURNING
                        1
                )
                SELECT
                    count(*)::integer
                FROM
                    kept;
                """;
            sql.Parameters.Add(new LibraryRenamePlanStateData(plan.BuiltAt, Fingerprint(plan.Renames), plan.Renames.Count));
            if ((int)(await ctx.Sql.ExecuteScalarAsync(sql, ct))! == 0)
            {
                return false;
            }

            sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO "LibraryRenamePlanItems" ("Position", "StoredFileId", "MediaStoredFileId", "LibraryRootId", "LibraryRootName", "RelativePath", "TargetPath")
                SELECT
                    item.position::integer,
                    (item.value ->> 'S')::bigint,
                    (item.value ->> 'M')::bigint,
                    (item.value ->> 'L')::bigint,
                    item.value ->> 'N',
                    item.value ->> 'P',
                    item.value ->> 'T'
                FROM
                    jsonb_array_elements(@Payload::jsonb) WITH ORDINALITY AS item (value, position);
                """;
            sql.Parameters.Add(new LibraryRenamePlanItemsData(JsonSerializer.Serialize(items)));
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            return true;
        }

        // Whether the worker builds the plan again: there is none yet, something asked for it, a scan or download finished since, or it is an hour old.
        public static async Task<bool> IsLibraryRenamePlanDueAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT NOT EXISTS (
                    SELECT
                        1
                    FROM
                        "LibraryRenamePlans" AS plan
                    WHERE
                        plan."RequestedAt" IS NULL
                        AND plan."BuiltAt" > now() - interval '1 hour'
                        AND NOT EXISTS (
                            SELECT
                                1
                            FROM
                                "Operations" AS operation
                            WHERE
                                operation."OperationTypeId" IN (1, 2)
                                AND operation."OperationStatusTypeId" = 3
                                AND operation."FinishedAt" > plan."BuiltAt"
                        )
                );
                """;
            return (bool)(await ctx.ReadSql.ExecuteScalarAsync(sql, ct))!;
        }

        // Something the plan reads changed: the worker builds it again soon. Without a plan there is nothing to mark; the worker builds the first one.
        public static Task<int> RequestLibraryRenamePlanAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "LibraryRenamePlans"
                SET
                    "RequestedAt" = now()
                WHERE
                    "RequestedAt" IS NULL;
                """;
            return ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // One page of the kept plan in the order it was planned, read with the plan's figures in one statement; no row before the first build.
        public static async Task<(LibraryRenamePlanStateData? Plan, PageResult<LibraryRenameV1> Page)> ReadLibraryRenamePlanPageAsync(this LogicContext ctx, PageRequest page,
            CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    plan."BuiltAt",
                    plan."Fingerprint",
                    plan."RenameCount",
                    item."StoredFileId",
                    item."LibraryRootName",
                    item."RelativePath",
                    item."TargetPath"
                FROM
                    "LibraryRenamePlans" AS plan
                    LEFT JOIN LATERAL (
                        SELECT
                            item."Position",
                            item."StoredFileId",
                            item."LibraryRootName",
                            item."RelativePath",
                            item."TargetPath"
                        FROM
                            "LibraryRenamePlanItems" AS item
                        WHERE
                            item."Position" > @Offset
                        ORDER BY
                            item."Position"
                        LIMIT @PageSize
                    ) AS item ON TRUE
                ORDER BY
                    item."Position";
                """;
            sql.Page = page;
            var rows = await ctx.ReadSql.ReadPageAsync<PlanRow>(sql, ct);
            if (rows.Items.Count == 0)
            {
                return (null, new PageResult<LibraryRenameV1>([], page));
            }

            var first = rows.Items[0];
            var items = rows.Items.Where(row => row.StoredFileId is not null)
                .Select(row => new LibraryRenameV1(row.StoredFileId!.Value, row.LibraryRootName!, row.RelativePath!, row.TargetPath!)).ToArray();
            return (new LibraryRenamePlanStateData(first.BuiltAt, first.Fingerprint, first.RenameCount), new PageResult<LibraryRenameV1>(items, page));
        }

        // The plan's figures beside one of its renames; the rename's columns are empty when the page holds none.
        private sealed record PlanRow(DateTime BuiltAt, string Fingerprint, int RenameCount, long? StoredFileId, string? LibraryRootName, string? RelativePath, string? TargetPath);

        // Short names keep the JSON of a large plan small.
        private sealed record PlanItem(long S, long M, long L, string N, string P, string T);
    }
}
