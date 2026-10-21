using Jularr.Backend.Data.Admin.System.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.System.V1
{
    // The figures of the Administration's first page: what the library holds, the requests that wait, and what each library connection's volume holds.
    [ServiceEndpoint("GET", "/api/v1/admin/system/dashboard")]
    public sealed class ReadDashboardV1 : AdminService<ReadDashboardParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<DashboardV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadDashboardParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadDashboardParametersV1 parameters, Type resultType) => new("instance.settings.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Stored state only; nothing here asks storage or providers.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    (
                        SELECT
                            count(DISTINCT asset."WorkId")
                        FROM
                            "StoredFiles" AS file
                            JOIN "MediaAssets" AS asset ON asset."Id" = file."MediaAssetId"
                        WHERE
                            file."MissingSince" IS NULL
                    ) AS "Works",
                    (SELECT count(*) FROM "StoredFiles" AS file WHERE file."MissingSince" IS NULL) AS "Files",
                    (SELECT count(*) FROM "AcquisitionRequests" AS request WHERE request."AcquisitionRequestStatusTypeId" = 1) AS "OpenRequests";
                """;
            var totals = await Context.ReadSql.ReadRequiredAsync<DashboardTotalsData>(sql, CancellationToken);

            // Movies and series are video; books, music and games each stand for themselves.
            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    mount."Id" AS "StorageMountId",
                    mount."Name",
                    state."TotalBytes",
                    state."FreeBytes",
                    coalesce(sum(file."SizeBytes") FILTER (WHERE root."MediaTypeId" IN (1, 2)), 0)::bigint AS "VideoBytes",
                    coalesce(sum(file."SizeBytes") FILTER (WHERE root."MediaTypeId" = 3), 0)::bigint AS "BookBytes",
                    coalesce(sum(file."SizeBytes") FILTER (WHERE root."MediaTypeId" = 6), 0)::bigint AS "MusicBytes",
                    coalesce(sum(file."SizeBytes") FILTER (WHERE root."MediaTypeId" = 7), 0)::bigint AS "GameBytes"
                FROM
                    "StorageMounts" AS mount
                    JOIN "LibraryRoots" AS root ON root."StorageMountId" = mount."Id"
                    LEFT JOIN "StorageMountStates" AS state ON state."StorageMountId" = mount."Id"
                    LEFT JOIN "StoredFiles" AS file ON file."LibraryRootId" = root."Id" AND file."MissingSince" IS NULL
                GROUP BY
                    mount."Id",
                    mount."Name",
                    state."TotalBytes",
                    state."FreeBytes"
                ORDER BY
                    mount."Name",
                    mount."Id";
                """;
            var storage = await Context.ReadSql.ReadListAsync<DashboardStorageV1>(sql, CancellationToken);
            return new DashboardV1(totals.Works, totals.Files, totals.OpenRequests, new BaseDataList<DashboardStorageV1>(storage));
        }
    }
}
