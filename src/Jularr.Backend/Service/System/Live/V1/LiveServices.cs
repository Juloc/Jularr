using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.System.Live.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.System.Live.V1
{
    // The cheap check behind the live streams: one small row that changes whenever something the live views show changes.
    public sealed class ReadLiveVersionV1 : SystemService<ReadLiveVersionParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Read;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<LiveVersionV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadLiveVersionParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadLiveVersionParametersV1 parameters, Type resultType) => new("system.live");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Running operations with everything they report, the newest operation, requests and history.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    md5(
                        coalesce((
                            SELECT
                                string_agg(concat_ws(':', operation."Id", operation."OperationStatusTypeId", operation."ProgressPercent", operation."SizeBytes", operation."RemainingBytes",
                                    operation."EtaSeconds", operation."DownloadStateTypeId", operation."AttentionKey", operation."ErrorKey", operation."HintKey"), ',' ORDER BY operation."Id")
                            FROM
                                "Operations" AS operation
                            WHERE
                                operation."OperationStatusTypeId" IN (1, 2)
                        ), '')
                        || '|' || coalesce((SELECT max(operation."Id") FROM "Operations" AS operation)::text, '')
                        || '|' || (SELECT count(*) FROM "Operations" AS operation WHERE operation."OperationStatusTypeId" = 4 AND operation."DismissedAt" IS NULL)::text
                        || '|' || coalesce((SELECT max(request."UpdatedAt") FROM "AcquisitionRequests" AS request)::text, '') || ':' || (SELECT count(*) FROM "AcquisitionRequests")::text
                        || '|' || coalesce((SELECT max(event."Id") FROM "ActivityEvents" AS event)::text, '')
                        || '|' || (SELECT count(*) FROM "WorkerJobs" AS job WHERE job."LastResultTypeId" = 2)::text
                    ) AS "Version",
                    EXISTS (
                        SELECT
                            1
                        FROM
                            "Operations" AS operation
                        WHERE
                            operation."OperationTypeId" = 2
                            AND operation."OperationStatusTypeId" IN (1, 2)
                    ) AS "HasDownloads",
                    EXISTS (
                        SELECT
                            1
                        FROM
                            "Operations" AS operation
                        WHERE
                            operation."OperationTypeId" <> 2
                            AND operation."OperationStatusTypeId" IN (1, 2)
                    ) AS "HasJobs";
                """;
            return await Context.ReadSql.ReadRequiredAsync<LiveVersionV1>(sql, CancellationToken);
        }
    }
}
