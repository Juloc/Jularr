using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Operation;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Operation;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Activity.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/activity")]
    public sealed class ReadActivityV1 : AdminService<ReadActivityParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<ActivityItemV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadActivityParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadActivityParametersV1 parameters, Type resultType) => new("media.read");
        protected override PageRequest GetPageRequest(ReadActivityParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // Everything in the queue, one state of it, or what has finished; what needs a person comes first.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    operation."Id",
                    operation."OperationTypeId",
                    operation."OperationStatusTypeId",
                    coalesce(release."Title", root."Name", CASE WHEN operation."OperationTypeId" = 6 THEN 'Media import' END, operation."WorkerJobKey", '') AS "Title",
                    work."Id" AS "WorkId",
                    work."CanonicalTitle" AS "WorkTitle",
                    operation."ProgressPercent"::integer AS "ProgressPercent",
                    coalesce(operation."AttentionKey", operation."ErrorKey") AS "ReasonKey",
                    operation."CreatedAt",
                    operation."FinishedAt",
                    connection."Name" AS "ConnectionName",
                    indexer."Name" AS "IndexerName",
                    operation."SizeBytes",
                    operation."RemainingBytes",
                    operation."EtaSeconds",
                    CASE WHEN operation."DownloadStateTypeId" = 2 AND operation."EtaSeconds" > 0 THEN operation."RemainingBytes" / operation."EtaSeconds" END AS "BytesPerSecond",
                    operation."DownloadStateTypeId",
                    operation."AttentionUrl" AS "ManualUrl",
                    operation."OperationTypeId" = 2 AND operation."OperationStatusTypeId" = 2 AND operation."AttentionKey" IS NULL
                        AND coalesce(connection."AcquisitionConnectionTypeId" IN (2, 3, 4, 5, 6, 9), FALSE) AS "CanPause",
                    coalesce(connection."AcquisitionConnectionTypeId" = 9 AND operation."OperationStatusTypeId" IN (1, 2)
                        OR connection."AcquisitionConnectionTypeId" = 2 AND operation."OperationTypeId" = 2
                            AND operation."ExternalId" IS NOT NULL, FALSE) AS "CanBrowseFiles",
                    CASE WHEN connection."AcquisitionConnectionTypeId" = 2 THEN connection."Id" END AS "SabnzbdConnectionId",
                    operation."OperationTypeId" = 2 AND operation."OperationStatusTypeId" IN (1, 2) AND operation."AttentionKey" IS NOT NULL
                        AND operation."AttentionUrl" IS NULL AS "CanRetryImport",
                    operation."OperationStatusTypeId" IN (1, 2) AND operation."AttentionKey" IS NOT NULL AS "IsWaiting",
                    operation."OperationTypeId" = 2 AND operation."OperationStatusTypeId" IN (1, 2) AND operation."AttentionProposal" -> 'Source' IS NOT NULL AS "CanImportManually"
                FROM
                    "Operations" AS operation
                    LEFT JOIN "LibraryRoots" AS root ON root."Id" = operation."LibraryRootId"
                    LEFT JOIN "AcquisitionReleases" AS release ON release."Id" = operation."AcquisitionReleaseId"
                    LEFT JOIN "AcquisitionConnections" AS connection ON connection."Id" = operation."AcquisitionConnectionId"
                    LEFT JOIN "AcquisitionConnections" AS indexer ON indexer."Id" = release."AcquisitionConnectionId"
                    JOIN "OperationQueueStates" AS state ON state."OperationId" = operation."Id"
                    LEFT JOIN "WantedItems" AS wanted ON wanted."Id" = release."WantedItemId"
                    LEFT JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                WHERE
                    (
                        @ActivityFilterTypeId = 1 AND state."ActivityFilterTypeId" <> 3
                        OR state."ActivityFilterTypeId" = @ActivityFilterTypeId
                    )
                    -- The Files view includes all supported external download clients.
                    AND (NOT @TorrentOnly OR connection."AcquisitionConnectionTypeId" IN (2, 9))
                ORDER BY
                    -- What needs a person comes first, then what runs, each newest first.
                    @ActivityFilterTypeId <> 3 AND state."ActivityFilterTypeId" IN (4, 6) DESC,
                    operation."CreatedAt" DESC,
                    operation."Id" DESC
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new ActivityFilterData(Parameters.ActivityFilterTypeId, Parameters.TorrentOnly));
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<ActivityItemV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/activity/summary")]
    public sealed class ReadActivitySummaryV1 : AdminService<ReadActivitySummaryParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ActivitySummaryV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadActivitySummaryParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadActivitySummaryParametersV1 parameters, Type resultType) => new("media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The numbers of the queue's tiles; the pace is what the downloading jobs still have left over the time they still need.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    count(*) FILTER (WHERE state."ActivityFilterTypeId" = 2)::integer AS "ActiveCount",
                    coalesce(sum(operation."RemainingBytes" / operation."EtaSeconds") FILTER (WHERE operation."DownloadStateTypeId" = 2 AND operation."EtaSeconds" > 0 AND state."ActivityFilterTypeId" <> 3), 0)::bigint AS "BytesPerSecond",
                    count(*) FILTER (WHERE state."ActivityFilterTypeId" IN (4, 5))::integer AS "ImportCount",
                    count(*) FILTER (WHERE state."ActivityFilterTypeId" = 6)::integer AS "ErrorCount",
                    count(*) FILTER (WHERE state."ActivityFilterTypeId" <> 3)::integer AS "AllCount",
                    count(*) FILTER (WHERE state."ActivityFilterTypeId" = 4)::integer AS "WaitingCount",
                    count(*) FILTER (WHERE state."ActivityFilterTypeId" = 5)::integer AS "UnpackingCount",
                    count(*) FILTER (WHERE state."ActivityFilterTypeId" = 3)::integer AS "HistoryCount"
                FROM
                    "OperationQueueStates" AS state
                    JOIN "Operations" AS operation ON operation."Id" = state."OperationId";
                """;
            return await Context.ReadSql.ReadRequiredAsync<ActivitySummaryV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/activity/events")]
    public sealed class ReadActivityEventsV1 : AdminService<ReadActivityEventsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<ActivityEventV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadActivityEventsParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadActivityEventsParametersV1 parameters, Type resultType) => new("media.read");
        protected override PageRequest GetPageRequest(ReadActivityEventsParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // Newest first, for one Work or all.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    event."Id",
                    event."ActivityEventTypeId",
                    event."OccurredAt",
                    event."WorkId",
                    event."WorkTitle",
                    event."WorkUnitTypeId",
                    event."ParentUnitNumber",
                    event."UnitNumber",
                    event."ReleaseTitle",
                    event."QualityKey",
                    event."IndexerName",
                    event."ClientName",
                    event."Path",
                    event."PreviousPath",
                    event."ReasonKey",
                    account."DisplayName" AS "AccountName"
                FROM
                    "ActivityEvents" AS event
                    LEFT JOIN "Accounts" AS account ON account."Id" = event."AccountId"
                WHERE
                    (@WorkId::bigint IS NULL OR event."WorkId" = @WorkId)
                    AND (
                        @ActivityEventGroupTypeId = 0
                        OR @ActivityEventGroupTypeId = CASE
                            WHEN event."ActivityEventTypeId" = 1 THEN 1
                            WHEN event."ActivityEventTypeId" IN (3, 4, 7, 9) THEN 2
                            WHEN event."ActivityEventTypeId" IN (2, 8) THEN 3
                            ELSE 4
                        END
                    )
                ORDER BY
                    event."OccurredAt" DESC,
                    event."Id" DESC
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new ActivityWorkData(Parameters.WorkId, Parameters.ActivityEventGroupTypeId));
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<ActivityEventV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/activity/events/summary")]
    public sealed class ReadActivityEventsSummaryV1 : AdminService<ReadActivityEventsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ActivityEventsSummaryV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadActivityEventsParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadActivityEventsParametersV1 parameters, Type resultType) => new("media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The chips of the history, for one Work or all (the group and the page of the list do not count).
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    count(*)::integer AS "TotalCount",
                    count(*) FILTER (WHERE event."ActivityEventTypeId" = 1)::integer AS "GrabbedCount",
                    count(*) FILTER (WHERE event."ActivityEventTypeId" IN (3, 4, 7, 9))::integer AS "ArrivedCount",
                    count(*) FILTER (WHERE event."ActivityEventTypeId" IN (2, 8))::integer AS "FailedCount",
                    count(*) FILTER (WHERE event."ActivityEventTypeId" NOT IN (1, 2, 3, 4, 7, 8, 9))::integer AS "FilesCount"
                FROM
                    "ActivityEvents" AS event
                WHERE
                    @WorkId::bigint IS NULL
                    OR event."WorkId" = @WorkId;
                """;
            sql.Parameters.Add(new ActivityWorkData(Parameters.WorkId));
            return await Context.ReadSql.ReadRequiredAsync<ActivityEventsSummaryV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/activity/{operationId:long}/retry")]
    public sealed class RetryActivityV1 : AdminService<ActivityOperationParametersV1, NoData>
    {
        private readonly WorkSignals _signals;

        internal RetryActivityV1(WorkSignals signals)
        {
            _signals = signals;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ActivityOperationParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ActivityOperationParametersV1 parameters, Type resultType) => new("storage.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.RetryOperationAsync(new OperationActionData(Parameters.OperationId, Auth.AccountId), ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }

        // A retried scan or backup is queued again.
        protected override void OnCommitted()
        {
            _signals.Scans.Notify();
            _signals.Backups.Notify();
            _signals.Benchmarks.Notify();
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/activity/{operationId:long}/source-link")]
    public sealed class SaveActivitySourceLinkV1 : AdminService<ActivityOperationParametersV1, SaveActivitySourceLinkDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ActivityOperationParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ActivityOperationParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.SetSourceLinkAsync(new SourceLinkData(Parameters.OperationId, Data.SourceLinkUrl, null, null, null), ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/activity/{operationId:long}/dismiss")]
    public sealed class DismissActivityV1 : AdminService<ActivityOperationParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ActivityOperationParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ActivityOperationParametersV1 parameters, Type resultType) => new("storage.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.DismissOperationAsync(new OperationActionData(Parameters.OperationId, Auth.AccountId), ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
