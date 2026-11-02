using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Media
{
    // Deleting a title as Sonarr does: its requests, targets and releases go, the history keeps its texts, and an exclusion may keep it from coming back.
    internal static class WorkDeletionLogic
    {
        public static Task<WorkDeletionFactsData> ReadWorkDeletionFactsAsync(this LogicContext ctx, WorkParametersV1 parameters, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    coalesce((
                        SELECT
                            array_agg(operation."Id" ORDER BY operation."Id")
                        FROM
                            "Operations" AS operation
                            JOIN "AcquisitionReleases" AS release ON release."Id" = operation."AcquisitionReleaseId"
                            JOIN "WantedItems" AS wanted ON wanted."Id" = release."WantedItemId"
                        WHERE
                            wanted."WorkId" = work."Id"
                            AND operation."OperationTypeId" = 2
                            AND operation."OperationStatusTypeId" IN (1, 2)
                    ), '{}') AS "ActiveDownloadIds",
                    coalesce((
                        SELECT
                            array_agg(file."Id" ORDER BY file."Id")
                        FROM
                            "StoredFiles" AS file
                            JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                        WHERE
                            asset."WorkId" = work."Id"
                    ), '{}') AS "StoredFileIds"
                FROM
                    "Works" AS work
                WHERE
                    work."Id" = @WorkId;
                """;
            sql.Parameters.Add(parameters);
            return ctx.ReadSql.ReadRequiredAsync<WorkDeletionFactsData>(sql, ct);
        }

        public static async Task DeleteWorkAsync(this LogicContext ctx, WorkDeletionData data, CancellationToken ct)
        {
            // The exclusion keeps every provider identity of the title, so no list or request brings it back.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH excluded AS (
                    INSERT INTO
                        "WorkExclusions" ("MediaTypeId", "Title", "ReleaseYear")
                    SELECT
                        work."MediaTypeId",
                        work."CanonicalTitle",
                        work."ReleaseYear"
                    FROM
                        "Works" AS work
                    WHERE
                        @AddsExclusion
                        AND work."Id" = @WorkId
                        AND EXISTS (
                            SELECT
                                1
                            FROM
                                "WorkExternalIdentities" AS identity
                            WHERE
                                identity."WorkId" = work."Id"
                        )
                    RETURNING
                        "Id"
                )
                INSERT INTO
                    "WorkExclusionIdentities" ("WorkExclusionId", "Namespace", "Value")
                SELECT
                    excluded."Id",
                    identity."Namespace",
                    identity."Value"
                FROM
                    excluded
                    JOIN "WorkExternalIdentities" AS identity ON identity."WorkId" = @WorkId
                ON CONFLICT ("Namespace", "Value") DO NOTHING;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            // Requests and targets restrict the title, so they leave first, one statement after the other.
            foreach (var statement in new[]
            {
                """
                DELETE FROM
                    "RequestTargets" AS target
                WHERE
                    target."WantedItemId" IN (
                        SELECT
                            wanted."Id"
                        FROM
                            "WantedItems" AS wanted
                        WHERE
                            wanted."WorkId" = @WorkId
                    );
                """,
                """
                DELETE FROM
                    "AcquisitionRequests" AS request
                WHERE
                    request."WorkId" = @WorkId;
                """,
                """
                DELETE FROM
                    "WantedItems" AS wanted
                WHERE
                    wanted."WorkId" = @WorkId;
                """,
                """
                DELETE FROM
                    "Works" AS work
                WHERE
                    work."Id" = @WorkId;
                """
            })
            {
                sql = new SqlStatement { Statement = statement };
                sql.Parameters.Add(data);
                await ctx.ExecuteAsyncForAffectedRows(sql, ct);
            }
        }

        // A title the administrator excluded cannot be requested through any of its provider identities.
        public static async Task RequireNotExcludedAsync(this LogicContext ctx, string @namespace, string value, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    identity."Namespace",
                    identity."Value"
                FROM
                    "WorkExclusionIdentities" AS identity
                WHERE
                    identity."Namespace" = @Namespace
                    AND identity."Value" = @Value;
                """;
            sql.Parameters.Add(new ExclusionIdentityData(@namespace, value));
            if (await ctx.ReadSql.ReadOptionalAsync<ExclusionIdentityData>(sql, ct) is not null)
            {
                throw new DataValidationException(nameof(ExclusionIdentityData.Value), "excluded");
            }
        }

        // The same rule for a Work already kept: any of its provider identities that was excluded refuses the request.
        public static async Task RequireWorkNotExcludedAsync(this LogicContext ctx, long workId, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    exclusion."Namespace",
                    exclusion."Value"
                FROM
                    "WorkExternalIdentities" AS identity
                    JOIN "WorkExclusionIdentities" AS exclusion ON exclusion."Namespace" = identity."Namespace" AND exclusion."Value" = identity."Value"
                WHERE
                    identity."WorkId" = @Id
                LIMIT 1;
                """;
            sql.Parameters.Add(new WorkIdData(workId));
            if (await ctx.ReadSql.ReadOptionalAsync<ExclusionIdentityData>(sql, ct) is not null)
            {
                throw new DataValidationException(nameof(ExclusionIdentityData.Value), "excluded");
            }
        }

        public static async Task DeleteWorkExclusionAsync(this LogicContext ctx, WorkExclusionParametersV1 parameters, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "WorkExclusions" AS exclusion
                WHERE
                    exclusion."Id" = @WorkExclusionId;
                """;
            sql.Parameters.Add(parameters);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) == 0)
            {
                throw new KeyNotFoundException("The exclusion was not found.");
            }
        }
    }
}
