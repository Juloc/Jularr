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
            // Running, waiting for a person, finished.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    operation."Id",
                    operation."OperationTypeId",
                    operation."OperationStatusTypeId",
                    coalesce(release."Title", root."Name", operation."WorkerJobKey", '') AS "Title",
                    work."Id" AS "WorkId",
                    work."CanonicalTitle" AS "WorkTitle",
                    operation."ProgressPercent"::integer AS "ProgressPercent",
                    coalesce(operation."AttentionKey", operation."ErrorKey") AS "ReasonKey",
                    operation."CreatedAt",
                    operation."FinishedAt",
                    connection."Name" AS "ConnectionName",
                    operation."SizeBytes",
                    operation."RemainingBytes",
                    operation."EtaSeconds",
                    operation."DownloadStateTypeId",
                    operation."AttentionUrl" AS "ManualUrl",
                    operation."OperationTypeId" = 2 AND operation."OperationStatusTypeId" = 2 AND operation."AttentionKey" IS NULL
                        AND connection."AcquisitionConnectionTypeId" IN (2, 3, 4, 5, 6) AS "CanPause",
                    operation."OperationTypeId" = 2 AND operation."OperationStatusTypeId" IN (1, 2) AND operation."AttentionKey" IS NOT NULL
                        AND operation."AttentionUrl" IS NULL AS "CanRetryImport",
                    operation."OperationStatusTypeId" IN (1, 2) AND operation."AttentionKey" IS NOT NULL AS "IsWaiting",
                    operation."OperationTypeId" = 2 AND operation."OperationStatusTypeId" IN (1, 2) AND operation."AttentionProposal" -> 'Source' IS NOT NULL AS "CanImportManually"
                FROM
                    "Operations" AS operation
                    LEFT JOIN "LibraryRoots" AS root ON root."Id" = operation."LibraryRootId"
                    LEFT JOIN "AcquisitionReleases" AS release ON release."Id" = operation."AcquisitionReleaseId"
                    LEFT JOIN "AcquisitionConnections" AS connection ON connection."Id" = operation."AcquisitionConnectionId"
                    LEFT JOIN "WantedItems" AS wanted ON wanted."Id" = release."WantedItemId"
                    LEFT JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                WHERE
                    (CASE
                        WHEN operation."OperationStatusTypeId" IN (1, 2) OR operation."DismissedAt" IS NULL AND operation."OperationStatusTypeId" = 4 THEN 1
                        ELSE 3
                    END) = @ActivityTabTypeId
                ORDER BY
                    -- What needs a person comes first, then what runs, each newest first.
                    @ActivityTabTypeId = 1 AND (operation."OperationStatusTypeId" = 4 OR operation."AttentionKey" IS NOT NULL) DESC,
                    operation."CreatedAt" DESC,
                    operation."Id" DESC
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new ActivityFilterData(Parameters.ActivityTabTypeId));
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<ActivityItemV1>(sql, CancellationToken);
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
                    @WorkId::bigint IS NULL
                    OR event."WorkId" = @WorkId
                ORDER BY
                    event."OccurredAt" DESC,
                    event."Id" DESC
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new ActivityWorkData(Parameters.WorkId));
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<ActivityEventV1>(sql, CancellationToken);
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
