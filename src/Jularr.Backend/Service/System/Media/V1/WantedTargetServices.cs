using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.System.Media.V1
{
    // Every title with files has a target, so its page offers the same buttons whatever way it came into the library (a media server's sync, a scan). A new target is paused:
    // nothing is searched until the Owner monitors the title, and it lies in the library of its files. Games are monitored per platform.
    public sealed class EnsureWantedTargetsV1 : SystemService<CoverParametersV1, NoData>
    {
        private const int PassSize = 500;

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(CoverParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(CoverParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var created = await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    $"""
                    INSERT INTO "WantedItems" (
                        "WorkId",
                        "LibraryRootId",
                        "IsPaused"
                    )
                    SELECT
                        found."WorkId",
                        found."LibraryRootId",
                        TRUE
                    FROM (
                        SELECT DISTINCT ON (work."Id")
                            work."Id" AS "WorkId",
                            root."Id" AS "LibraryRootId"
                        FROM
                            "Works" AS work
                            JOIN "MediaAssets" AS asset ON asset."WorkId" = work."Id"
                            JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id"
                            JOIN "LibraryRoots" AS root ON root."Id" = file."LibraryRootId"
                                AND root."MediaTypeId" = work."MediaTypeId"
                        WHERE
                            work."MediaTypeId" <> 7
                            AND file."MissingSince" IS NULL
                            AND NOT EXISTS (
                                SELECT
                                    1
                                FROM
                                    "WantedItems" AS wanted
                                WHERE
                                    wanted."WorkId" = work."Id"
                            )
                        ORDER BY
                            work."Id",
                            file."Id"
                        LIMIT {PassSize}
                    ) AS found
                    ON CONFLICT ("WorkId", "GamePlatformId") DO NOTHING;
                    """;
                return ctx.ExecuteAsyncForAffectedRows(sql, ct);
            }, CancellationToken);
            return new UpdateResult(created >= PassSize);
        }
    }
}
