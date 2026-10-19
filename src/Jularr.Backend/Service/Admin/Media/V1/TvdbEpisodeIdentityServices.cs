using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    [ServiceEndpoint("POST", "/api/v1/admin/works/{workId:long}/tvdb-episodes")]
    public sealed class LinkTvdbEpisodeIdentityV1 : AdminService<WorkParametersV1, LinkTvdbEpisodeIdentityDataV1>
    {
        private readonly TvdbMetadata _tvdb;

        internal LinkTvdbEpisodeIdentityV1(TvdbMetadata tvdb)
        {
            _tvdb = tvdb;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<WorkDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    identity."Namespace",
                    identity."Value",
                    identity."WorkId"
                FROM
                    "WorkExternalIdentities" AS identity
                    JOIN "Works" AS work ON work."Id" = identity."WorkId"
                WHERE
                    work."Id" = @Id
                    AND work."MediaTypeId" = 2
                    AND identity."Namespace" = 'tvdb';
                """;
            sql.Parameters.Add(new WorkIdData(Parameters.WorkId));
            var identity = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadSql.ReadOptionalAsync<WorkIdentityData>(sql, ct), CancellationToken);
            if (identity is null || !int.TryParse(identity.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var tvdbId) || tvdbId <= 0)
            {
                throw new DataValidationException(nameof(WorkParametersV1.WorkId), "unknown_reference");
            }

            var session = await _tvdb.ConnectAsync(Context, CancellationToken);
            var entries = await _tvdb.ReadEpisodesAsync(session, tvdbId, CancellationToken);
            if (entries.Count(episode => episode.Id == Data.TvdbEpisodeId) != 1)
            {
                throw new DataValidationException(nameof(Data.TvdbEpisodeId), "unknown_reference");
            }

            return await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.LinkTvdbEpisodeIdentityAsync(new TvdbEpisodeIdentityData(Parameters.WorkId, Data.WorkUnitId,
                    tvdbId, Data.TvdbEpisodeId, Data.Version), ct);
                return await WorkMetadataQueries.ReadWorkAsync(ctx, Parameters.WorkId, ct);
            }, CancellationToken);
        }
    }
}
