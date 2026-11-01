using Jularr.Backend.Data.Admin.Live.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Live;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Live;
using Jularr.Backend.Service.Core;
using OperationKind = Jularr.Backend.Data.Enums.Operation.OperationType;

namespace Jularr.Backend.Service.Admin.Live.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/live")]
    public sealed class ReadLiveAdminV1 : AdminService<ReadLiveAdminParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<LiveAdminV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadLiveAdminParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadLiveAdminParametersV1 parameters, Type resultType) => new("media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Open requests of every profile and those that ended a moment ago.
            var requestSql = new SqlStatement();
            requestSql.Statement =
                """
                SELECT
                    request."Id",
                    request."WorkId",
                    request."AcquisitionRequestStatusTypeId"
                FROM
                    "AcquisitionRequests" AS request
                WHERE
                    request."AcquisitionRequestStatusTypeId" BETWEEN 1 AND 5
                    OR request."UpdatedAt" > now() - interval '10 minutes'
                ORDER BY
                    request."Id" DESC
                LIMIT 500;
                """;
            var requests = await Context.ReadSql.ReadListAsync<LiveRequestRow>(requestSql, CancellationToken);

            // Running operations and those finished a moment ago, once for every request of their target.
            var operationSql = new SqlStatement();
            operationSql.Statement =
                """
                SELECT
                    operation."Id",
                    operation."OperationTypeId",
                    operation."OperationStatusTypeId",
                    operation."DownloadStateTypeId",
                    operation."OperationStatusTypeId" IN (1, 2) AND operation."AttentionKey" IS NOT NULL AS "IsWaiting",
                    operation."ProgressPercent"::integer AS "ProgressPercent",
                    operation."SizeBytes",
                    operation."RemainingBytes",
                    operation."EtaSeconds",
                    coalesce(operation."AttentionKey", operation."ErrorKey", operation."HintKey") AS "ReasonKey",
                    release."WantedItemId",
                    wanted."WorkId",
                    release."Title" AS "ReleaseTitle",
                    indexer."Name" AS "IndexerName",
                    client."Name" AS "ClientName",
                    target."AcquisitionRequestId"
                FROM
                    "Operations" AS operation
                    LEFT JOIN "AcquisitionReleases" AS release ON release."Id" = operation."AcquisitionReleaseId"
                    LEFT JOIN "AcquisitionConnections" AS indexer ON indexer."Id" = release."AcquisitionConnectionId"
                    LEFT JOIN "AcquisitionConnections" AS client ON client."Id" = operation."AcquisitionConnectionId"
                    LEFT JOIN "WantedItems" AS wanted ON wanted."Id" = release."WantedItemId"
                    LEFT JOIN "RequestTargets" AS target ON target."WantedItemId" = release."WantedItemId"
                WHERE
                    operation."OperationStatusTypeId" IN (1, 2)
                    OR operation."FinishedAt" > now() - interval '2 minutes'
                ORDER BY
                    operation."Id" DESC
                LIMIT 1000;
                """;
            var rows = await Context.ReadSql.ReadListAsync<LiveOperationRow>(operationSql, CancellationToken);

            // Each operation once.
            var operations = rows.GroupBy(row => row.Id).Select(group => group.First()).ToArray();
            var downloads = rows.Where(row => row.OperationTypeId == OperationKind.Download).ToArray();

            // Requests: phase and progress from the Logic owner, details from their latest running or waiting download.
            var requestDownloads = downloads.Where(row => row.AcquisitionRequestId is not null).ToLookup(row => row.AcquisitionRequestId!.Value);
            var requestItems = requests.Select(request =>
            {
                var own = requestDownloads[request.Id].OrderByDescending(row => row.Id).ToArray();
                var phase = LivePhases.OfRequest(request.AcquisitionRequestStatusTypeId, LivePhases.Combine(own.Select(Facts)));
                var current = own.FirstOrDefault(row => row.OperationStatusTypeId is OperationStatusType.Queued or OperationStatusType.Running);
                var reason = own.FirstOrDefault(row => row.ReasonKey is not null && (row.IsWaiting || row.OperationStatusTypeId is OperationStatusType.Failed or OperationStatusType.Queued or OperationStatusType.Running))?.ReasonKey;
                return new LiveAdminRequestV1(request.Id, request.WorkId, phase.Phase, phase.ProgressPercent, phase.EtaSeconds, phase.BytesPerSecond,
                    current?.ReleaseTitle, current?.IndexerName, current?.ClientName, reason);
            });

            // Targets: their running downloads together.
            var wantedItems = downloads.Where(row => row.WantedItemId is not null && row.WorkId is not null).GroupBy(row => row.Id).Select(group => group.First())
                .GroupBy(row => row.WantedItemId!.Value).Select(group =>
                {
                    var own = group.OrderByDescending(row => row.Id).ToArray();
                    var progress = LivePhases.Combine(own.Select(Facts));
                    var current = own.FirstOrDefault(row => row.OperationStatusTypeId is OperationStatusType.Queued or OperationStatusType.Running);
                    return progress is null || current is null ? null : new LiveWantedV1(group.Key, own[0].WorkId!.Value, LivePhases.OfTarget(progress), progress.Count, progress.IsWaiting, progress.IsPaused,
                        progress.ProgressPercent, progress.SizeBytes, progress.RemainingBytes, progress.EtaSeconds, progress.BytesPerSecond, current.ReleaseTitle, current.IndexerName, current.ClientName);
                }).OfType<LiveWantedV1>();

            return new LiveAdminV1(new BaseDataList<LiveAdminRequestV1>(requestItems),
                new BaseDataList<LiveOperationV1>(operations.Select(row => new LiveOperationV1(row.Id, row.OperationTypeId, row.OperationStatusTypeId, row.DownloadStateTypeId, row.IsWaiting, row.ProgressPercent,
                    row.SizeBytes, row.RemainingBytes, row.EtaSeconds, row.DownloadStateTypeId == DownloadStateType.Downloading ? LivePhases.Pace(row.RemainingBytes, row.EtaSeconds) : null, row.ReasonKey))),
                new BaseDataList<LiveWantedV1>(wantedItems));
        }

        private static LiveDownloadFacts Facts(LiveOperationRow row) => new(row.OperationStatusTypeId, row.DownloadStateTypeId, row.IsWaiting, row.ProgressPercent, row.SizeBytes, row.RemainingBytes, row.EtaSeconds);
    }
}
