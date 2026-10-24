using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Wanted.V1
{
    // A title that is in the library but was never requested (a media server's sync, a scan) starts being monitored like in Radarr: it gets its target, in the library of its files,
    // and the automatic search takes it up. The profile of the type applies until one is chosen.
    [ServiceEndpoint("POST", "/api/v1/admin/works/{workId:long}/monitoring")]
    public sealed class StartWorkMonitoringV1 : AdminService<WorkParametersV1, NoData>
    {
        private readonly AcquisitionSearchSignal _searchSignal;
        private bool _started;

        internal StartWorkMonitoringV1(AcquisitionSearchSignal searchSignal)
        {
            _searchSignal = searchSignal;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("wanted.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Games are monitored per platform.
            var work = await Context.ExecuteBatchAsync((ctx, ct) => WorkMetadataQueries.ReadWorkAsync(ctx, Parameters.WorkId, ct), CancellationToken);
            if (work.MediaTypeId == MediaType.Game)
            {
                throw new DataValidationException(nameof(WorkParametersV1.WorkId), "unknown_reference");
            }

            // The library its files lie in.
            _started = await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        file."LibraryRootId"
                    FROM
                        "MediaAssets" AS asset
                        JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id"
                    WHERE
                        asset."WorkId" = @Id
                    ORDER BY
                        file."Id"
                    LIMIT 1;
                    """;
                sql.Parameters.Add(new WorkIdData(Parameters.WorkId));
                var root = await ctx.ReadSql.ReadOptionalAsync<WorkLibraryRootData>(sql, ct);
                return await ctx.EnsureWantedItemAsync(new WantedTakeoverData(Parameters.WorkId, null, root?.LibraryRootId, false), ct);
            }, CancellationToken);
            return new UpdateResult(_started);
        }

        protected override void OnCommitted()
        {
            if (_started) _searchSignal.RequestSearch();
        }
    }
}
