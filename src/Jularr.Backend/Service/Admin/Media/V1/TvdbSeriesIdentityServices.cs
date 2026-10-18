using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    [ServiceEndpoint("POST", "/api/v1/admin/works/{workId:long}/tvdb-identity")]
    public sealed class LinkTvdbSeriesIdentityV1 : AdminService<WorkParametersV1, LinkTvdbSeriesIdentityDataV1>
    {
        private readonly TvdbMetadata _tvdb;

        internal LinkTvdbSeriesIdentityV1(TvdbMetadata tvdb)
        {
            _tvdb = tvdb;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<WorkDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var session = await _tvdb.ConnectAsync(Context, CancellationToken);
            var remote = await _tvdb.ReadSeriesAsync(session, Data.TvdbId, CancellationToken);
            if (remote.ImdbId is not { Length: > 0 })
            {
                throw new DataValidationException(nameof(Data.TvdbId), "unknown_reference");
            }

            return await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.LinkTvdbSeriesIdentityAsync(new TvdbIdentityLinkData(Parameters.WorkId, Data.TvdbId, Data.Version, remote.ImdbId), ct);
                return await WorkMetadataQueries.ReadWorkAsync(ctx, Parameters.WorkId, ct);
            }, CancellationToken);
        }
    }
}
