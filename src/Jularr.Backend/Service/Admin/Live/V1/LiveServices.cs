using System.Globalization;
using Jularr.Backend.Data.Admin.Live.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
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
    public sealed class ReadLiveAdminV1(ActiveSearches searches) : AdminService<ReadLiveAdminParametersV1>
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

            // What searches right now has no download yet: the target shows it, and so do the units it looks for.
            var searching = searches.Snapshot();
            var targets = wantedItems.ToList();
            if (searching.Count > 0)
            {
                var searched = await Context.ReadSql.ReadListAsync<LiveSearchTargetRow>(new SqlStatement
                {
                    Statement = "SELECT target.\"Id\" AS \"WantedItemId\", target.\"WorkId\" FROM \"WantedItems\" AS target WHERE target.\"Id\" IN (" + string.Join(", ", searching.Select(item => item.WantedItemId.ToString(CultureInfo.InvariantCulture)).Distinct()) + ");"
                }, CancellationToken);
                targets.AddRange(searched.Where(row => targets.All(target => target.Id != row.WantedItemId)).Select(row => new LiveWantedV1(row.WantedItemId, row.WorkId, AcquisitionRequestStatusType.Searching, 0, false, false, null, null, null, null, null, null, null, null)));
            }

            return new LiveAdminV1(new BaseDataList<LiveAdminRequestV1>(requestItems),
                new BaseDataList<LiveOperationV1>(operations.Select(row => new LiveOperationV1(row.Id, row.OperationTypeId, row.OperationStatusTypeId, row.DownloadStateTypeId, row.IsWaiting, row.ProgressPercent,
                    row.SizeBytes, row.RemainingBytes, row.EtaSeconds, row.DownloadStateTypeId == DownloadStateType.Downloading ? LivePhases.Pace(row.RemainingBytes, row.EtaSeconds) : null, row.ReasonKey))),
                new BaseDataList<LiveWantedV1>(targets), new BaseDataList<LiveUnitV1>(await ReadUnitsAsync(downloads, searching)));
        }

        // The units that something works on now: what a running download brings (the release's season and units), and what a running search looks for. A download wins over a search.
        // Nothing is read while nothing runs, so an idle instance costs no more than before.
        private async Task<LiveUnitV1[]> ReadUnitsAsync(LiveOperationRow[] downloads, IReadOnlyList<ActiveSearch> searching)
        {
            var units = new Dictionary<long, LiveUnitV1>();
            var active = downloads.Where(row => row.OperationStatusTypeId is OperationStatusType.Queued or OperationStatusType.Running).GroupBy(row => row.Id).ToDictionary(group => group.Key, group => group.First());
            if (active.Count > 0)
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        operation."Id" AS "OperationId",
                        unit."Id" AS "WorkUnitId",
                        unit."ParentWorkUnitId",
                        (
                            SELECT
                                count(*)::integer
                            FROM
                                "WorkUnits" AS sibling
                            WHERE
                                sibling."WorkId" = unit."WorkId"
                                AND sibling."WorkUnitTypeId" = unit."WorkUnitTypeId"
                                AND sibling."ParentWorkUnitId" IS NOT DISTINCT FROM unit."ParentWorkUnitId"
                        ) AS "SeasonUnitCount"
                    FROM
                        "Operations" AS operation
                        JOIN "AcquisitionReleases" AS release ON release."Id" = operation."AcquisitionReleaseId"
                        JOIN "WantedItems" AS target ON target."Id" = release."WantedItemId"
                        JOIN "WorkUnits" AS unit ON unit."WorkId" = target."WorkId" AND unit."WorkUnitTypeId" <> 1
                        LEFT JOIN "WorkUnits" AS parent ON parent."Id" = unit."ParentWorkUnitId"
                    WHERE
                        operation."OperationTypeId" = 2
                        AND operation."OperationStatusTypeId" IN (1, 2)
                        AND release."ParentUnitNumber" IS NOT DISTINCT FROM parent."Number"
                        AND (release."UnitStart" IS NULL OR unit."Number" BETWEEN release."UnitStart" AND release."UnitEnd")
                    LIMIT 5000;
                    """;
                foreach (var unit in (await Context.ReadSql.ReadListAsync<LiveDownloadUnitRow>(sql, CancellationToken)).GroupBy(row => row.WorkUnitId))
                {
                    var own = unit.Where(row => active.ContainsKey(row.OperationId)).Select(row => Facts(active[row.OperationId])).ToArray();
                    if (LivePhases.Combine(own) is not { } progress)
                    {
                        continue;
                    }

                    var phase = LivePhases.OfTarget(progress);
                    var first = unit.First();
                    units[unit.Key] = new LiveUnitV1(unit.Key, first.ParentWorkUnitId, first.SeasonUnitCount, phase, phase == AcquisitionRequestStatusType.Downloading ? progress.ProgressPercent : null);
                }
            }

            if (searching.Count > 0)
            {
                // A search of one unit marks it, whatever it has; a search of a season or of everything marks the units it would take: monitored, aired and without a file.
                var scopes = string.Join(", ", searching.Select(item => "(" + item.WantedItemId.ToString(CultureInfo.InvariantCulture) + "::bigint, " + Number(item.ParentNumber) + "::integer, " + Number(item.UnitNumber) + "::integer)"));
                var sql = new SqlStatement();
                sql.Statement =
                    $$"""
                    WITH search ("WantedItemId", "ParentNumber", "UnitNumber") AS (VALUES {{scopes}})
                    SELECT DISTINCT
                        unit."Id" AS "WorkUnitId",
                        unit."ParentWorkUnitId",
                        (
                            SELECT
                                count(*)::integer
                            FROM
                                "WorkUnits" AS sibling
                            WHERE
                                sibling."WorkId" = unit."WorkId"
                                AND sibling."WorkUnitTypeId" = unit."WorkUnitTypeId"
                                AND sibling."ParentWorkUnitId" IS NOT DISTINCT FROM unit."ParentWorkUnitId"
                        ) AS "SeasonUnitCount"
                    FROM
                        search
                        JOIN "WantedItems" AS target ON target."Id" = search."WantedItemId"
                        JOIN "WorkUnits" AS unit ON unit."WorkId" = target."WorkId" AND unit."WorkUnitTypeId" <> 1
                        LEFT JOIN "WorkUnits" AS parent ON parent."Id" = unit."ParentWorkUnitId"
                    WHERE
                        (search."ParentNumber" IS NULL OR parent."Number" = search."ParentNumber")
                        AND (search."UnitNumber" IS NULL OR unit."Number" = search."UnitNumber")
                        AND (
                            search."UnitNumber" IS NOT NULL
                            OR (
                                unit."IsMonitored"
                                AND (unit."WorkUnitTypeId" <> 2 OR unit."ReleasedOn" <= current_date)
                                AND NOT EXISTS (
                                    SELECT
                                        1
                                    FROM
                                        "MediaAssets" AS asset
                                        JOIN "StoredFiles" AS available ON available."MediaAssetId" = asset."Id" AND available."MissingSince" IS NULL AND NOT available."IsSubtitle"
                                    WHERE
                                        asset."WorkUnitId" = unit."Id"
                                )
                            )
                        )
                    LIMIT 5000;
                    """;
                foreach (var row in await Context.ReadSql.ReadListAsync<LiveSearchUnitRow>(sql, CancellationToken))
                {
                    units.TryAdd(row.WorkUnitId, new LiveUnitV1(row.WorkUnitId, row.ParentWorkUnitId, row.SeasonUnitCount, AcquisitionRequestStatusType.Searching, null));
                }
            }

            return [.. units.Values];
        }

        private static string Number(int? value) => value is { } number ? number.ToString(CultureInfo.InvariantCulture) : "NULL";

        private static LiveDownloadFacts Facts(LiveOperationRow row) => new(row.OperationStatusTypeId, row.DownloadStateTypeId, row.IsWaiting, row.ProgressPercent, row.SizeBytes, row.RemainingBytes, row.EtaSeconds);
    }
}
