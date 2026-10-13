using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Operation;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Operation;
using Jularr.Backend.Logic.Acquisition;
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
                    coalesce(release."Title", root."Name", '') AS "Title",
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
                    CASE
                        WHEN operation."AttentionKey" = 'source_manual_link'
                            AND EXISTS (
                                SELECT
                                    1
                                FROM
                                    "InstanceModules" AS assistance
                                WHERE
                                    assistance."Key" = 'SourceAssistance'
                                    AND assistance."IsEnabled"
                            )
                        THEN replace(release."SourceUrl", '/md5/', '/slow_download/') || '/0/0'
                    END AS "ManualUrl"
                FROM
                    "Operations" AS operation
                    LEFT JOIN "LibraryRoots" AS root ON root."Id" = operation."LibraryRootId"
                    LEFT JOIN "AcquisitionReleases" AS release ON release."Id" = operation."AcquisitionReleaseId"
                    LEFT JOIN "AcquisitionConnections" AS connection ON connection."Id" = operation."AcquisitionConnectionId"
                    LEFT JOIN "WantedItems" AS wanted ON wanted."Id" = release."WantedItemId"
                    LEFT JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                WHERE
                    CASE
                        WHEN operation."OperationStatusTypeId" IN (1, 2) AND operation."AttentionKey" IS NULL THEN 1
                        WHEN operation."OperationStatusTypeId" IN (1, 2) OR operation."DismissedAt" IS NULL AND operation."OperationStatusTypeId" = 4 THEN 2
                        ELSE 3
                    END = @ActivityTabTypeId
                ORDER BY
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

    [ServiceEndpoint("POST", "/api/v1/admin/activity/{operationId:long}/retry")]
    public sealed class RetryActivityV1 : AdminService<ActivityOperationParametersV1, NoData>
    {
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
    }

    [ServiceEndpoint("POST", "/api/v1/admin/activity/{operationId:long}/source-link")]
    public sealed class SaveActivitySourceLinkV1 : AdminService<ActivityOperationParametersV1, SaveActivitySourceLinkDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ActivityOperationParametersV1 parameters) => ModuleRequirement.All("Acquisition", "SourceAssistance");
        protected override ServicePermission GetPermission(ActivityOperationParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Files are fetched over HTTPS only.
            if (!Uri.TryCreate(Data.SourceUrl, UriKind.Absolute, out var link) || link.Scheme != Uri.UriSchemeHttps)
            {
                throw new DataValidationException(nameof(SaveActivitySourceLinkDataV1.SourceUrl), "invalid_url");
            }

            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.SetDownloadSourceLinkAsync(new DownloadSourceLinkData(Parameters.OperationId, link.ToString()), ct);
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
