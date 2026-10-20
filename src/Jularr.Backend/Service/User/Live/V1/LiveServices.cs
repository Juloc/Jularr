using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Live;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.User.Live.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Live;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Live.V1
{
    [ServiceEndpoint("GET", "/api/v1/live")]
    public sealed class ReadLiveV1 : UserService<ReadLiveParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<LiveRequestV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadLiveParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(ReadLiveParametersV1 parameters, Type resultType) => new("profile.requests.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The profile's own open requests and those that ended a moment ago.
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
                    request."RequestedByProfileId" = @ProfileId
                    AND (request."AcquisitionRequestStatusTypeId" BETWEEN 1 AND 5 OR request."UpdatedAt" > now() - interval '10 minutes')
                ORDER BY
                    request."Id" DESC
                LIMIT 200;
                """;
            requestSql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            var requests = await Context.ReadSql.ReadListAsync<LiveRequestRow>(requestSql, CancellationToken);

            // The running downloads of those that are searched or downloaded.
            var downloadSql = new SqlStatement();
            downloadSql.Statement =
                """
                SELECT
                    target."AcquisitionRequestId",
                    operation."OperationStatusTypeId",
                    operation."DownloadStateTypeId",
                    operation."AttentionKey" IS NOT NULL AS "IsWaiting",
                    operation."ProgressPercent"::integer AS "ProgressPercent",
                    operation."SizeBytes",
                    operation."RemainingBytes",
                    operation."EtaSeconds"
                FROM
                    "AcquisitionRequests" AS request
                    JOIN "RequestTargets" AS target ON target."AcquisitionRequestId" = request."Id"
                    JOIN "AcquisitionReleases" AS release ON release."WantedItemId" = target."WantedItemId"
                    JOIN "Operations" AS operation ON operation."AcquisitionReleaseId" = release."Id"
                        AND operation."OperationTypeId" = 2
                        AND operation."OperationStatusTypeId" IN (1, 2)
                WHERE
                    request."RequestedByProfileId" = @ProfileId
                    AND request."AcquisitionRequestStatusTypeId" BETWEEN 3 AND 5;
                """;
            downloadSql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            var downloads = (await Context.ReadSql.ReadListAsync<LiveRequestDownloadRow>(downloadSql, CancellationToken)).ToLookup(download => download.AcquisitionRequestId);

            // Phase and progress come from the one Logic owner.
            return new BaseDataList<LiveRequestV1>(requests.Select(request =>
            {
                var progress = LivePhases.Combine(downloads[request.Id].Select(download => new LiveDownloadFacts(download.OperationStatusTypeId, download.DownloadStateTypeId, download.IsWaiting,
                    download.ProgressPercent, download.SizeBytes, download.RemainingBytes, download.EtaSeconds)));
                var phase = LivePhases.OfRequest(request.AcquisitionRequestStatusTypeId, progress);
                return new LiveRequestV1(request.Id, request.WorkId, phase.Phase, phase.ProgressPercent, phase.EtaSeconds);
            }));
        }
    }
}
