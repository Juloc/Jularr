using System.Security.Cryptography;
using System.Text.Json;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    [ServiceEndpoint("POST", "/api/v1/admin/works/search-runs")]
    public sealed class QueueWorksSearchV1 : AdminService<WorksBulkParametersV1, QueueWorksSearchDataV1>
    {
        private readonly WorkSignals _signals;

        internal QueueWorksSearchV1(WorkSignals signals)
        {
            _signals = signals;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<SearchRunQueuedV1>()];
        protected override ModuleRequirement GetInstanceModules(WorksBulkParametersV1 parameters) => ModuleRequirement.All("Library", "Requests", "Acquisition");
        protected override ServicePermission GetPermission(WorksBulkParametersV1 parameters, Type resultType) => new("wanted.update");

        protected override async Task<IServiceOutput> OnCreate()
        {
            var key = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Auth.AccountId, Selection = Data })));
            var run = await Context.QueueWorksSearchAsync(new WorkSearchSelectionData(Data.WorkIds, Data.MediaTypeId, Data.IsMonitored, Data.WorkListStatusId, Data.LibraryRootId,
                Data.AcquisitionProfileId, Data.Query, Auth.AccountId, key), CancellationToken);
            return new SearchRunQueuedV1(run.OperationId, run.TargetCount);
        }

        protected override void OnCommitted() => _signals.Searches.Notify();
    }

    [ServiceEndpoint("GET", "/api/v1/admin/works/search-runs")]
    public sealed class ReadWorksSearchRunV1 : AdminService<ReadSearchRunParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<SearchRunDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadSearchRunParametersV1 parameters) => ModuleRequirement.All("Library", "Requests", "Acquisition");
        protected override ServicePermission GetPermission(ReadSearchRunParametersV1 parameters, Type resultType) => new("wanted.update");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    operation."Id" AS "OperationId",
                    run."TargetCount",
                    operation."ProcessedCount",
                    operation."OperationStatusTypeId",
                    run."IsCancelled"
                FROM "Operations" AS operation
                    JOIN "AcquisitionSearchRuns" AS run ON run."OperationId" = operation."Id"
                WHERE operation."RequestedByAccountId" = @AccountId
                    AND (@OperationId::bigint IS NULL OR operation."Id" = @OperationId)
                ORDER BY operation."CreatedAt" DESC, operation."Id" DESC
                LIMIT 1;
                """;
            sql.Parameters.Add(Parameters);
            sql.Parameters.Add(Auth);
            return new BaseDataList<SearchRunDetailsV1>(await Context.ReadSql.ReadListAsync<SearchRunDetailsV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/works/search-runs/{operationId:long}/results")]
    public sealed class ReadWorksSearchResultsV1 : AdminService<SearchRunParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<WorkBulkOutcomeV1>>()];
        protected override ModuleRequirement GetInstanceModules(SearchRunParametersV1 parameters) => ModuleRequirement.All("Library", "Requests", "Acquisition");
        protected override ServicePermission GetPermission(SearchRunParametersV1 parameters, Type resultType) => new("wanted.update");
        protected override PageRequest GetPageRequest(SearchRunParametersV1 parameters) => new(parameters.Page, 25);

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id",
                    work."CanonicalTitle",
                    coalesce(target."OutcomeKey", 'pending') AS "OutcomeKey"
                FROM "AcquisitionSearchTargets" AS target
                    JOIN "Operations" AS operation ON operation."Id" = target."OperationId" AND operation."RequestedByAccountId" = @AccountId
                    JOIN "WantedItems" AS wanted ON wanted."Id" = target."WantedItemId"
                    JOIN "Works" AS work ON work."Id" = wanted."WorkId"
                WHERE target."OperationId" = @OperationId
                ORDER BY target."WantedItemId"
                OFFSET @Offset LIMIT @PageSize;
                """;
            sql.Parameters.Add(new SearchRunKeyData(Parameters.OperationId));
            sql.Parameters.Add(Auth);
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<WorkBulkOutcomeV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/works/search-runs/{operationId:long}/cancel")]
    public sealed class CancelWorksSearchV1 : AdminService<SearchRunParametersV1, NoData>
    {
        private readonly SearchRunCancellation _cancellation;

        internal CancelWorksSearchV1(SearchRunCancellation cancellation)
        {
            _cancellation = cancellation;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(SearchRunParametersV1 parameters) => ModuleRequirement.All("Library", "Requests", "Acquisition");
        protected override ServicePermission GetPermission(SearchRunParametersV1 parameters, Type resultType) => new("wanted.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.CancelWorksSearchAsync(new SearchRunActionData(Parameters.OperationId, Auth.AccountId ?? throw new UnauthorizedAccessException()), CancellationToken);
            return new UpdateResult(true);
        }

        protected override void OnCommitted() => _cancellation.Cancel(Parameters.OperationId);
    }
}
