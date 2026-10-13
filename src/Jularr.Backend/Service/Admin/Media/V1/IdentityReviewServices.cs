using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/works/matches")]
    public sealed class ReadIdentityReviewsV1 : AdminService<ReadIdentityReviewsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<IdentityReviewV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadIdentityReviewsParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReadIdentityReviewsParametersV1 parameters, Type resultType) => new("media.read");
        protected override PageRequest GetPageRequest(ReadIdentityReviewsParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            // Oldest waiting matches first.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id" AS "WorkId",
                    work."CanonicalTitle",
                    work."MediaTypeId",
                    work."MediaSubtypeId",
                    ARRAY(SELECT (candidate ->> 'Id')::integer FROM jsonb_array_elements(match."Candidates") AS candidate) AS "CandidateIds",
                    ARRAY(
                        SELECT
                            concat_ws(' · ', candidate ->> 'Title', candidate ->> 'Year', candidate ->> 'Format')
                        FROM
                            jsonb_array_elements(match."Candidates") AS candidate
                    ) AS "CandidateLabels"
                FROM
                    "WorkIdentityMatches" AS match
                    JOIN "Works" AS work ON work."Id" = match."WorkId"
                WHERE
                    match."IdentityMatchStatusTypeId" = 2
                ORDER BY
                    match."CheckedAt",
                    work."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<IdentityReviewV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/works/{workId:long}/matches")]
    public sealed class ResolveIdentityReviewV1 : AdminService<IdentityReviewParametersV1, ResolveIdentityReviewDataV1>
    {
        private readonly AniListMatcher _matcher;

        internal ResolveIdentityReviewV1(AniListMatcher matcher)
        {
            _matcher = matcher;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(IdentityReviewParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(IdentityReviewParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Only a waiting match is decided; a dismissed one is not tried again.
            var target = (await Context.ExecuteBatchAsync((ctx, ct) => AniListMatcher.ReadTargetsAsync(ctx, Parameters.WorkId, 1, ct), CancellationToken)).SingleOrDefault()
                ?? throw new KeyNotFoundException("No match of this Work waits for a decision.");
            var result = Data.AniListId is { } chosen
                ? await _matcher.MatchAsync(target, target.MediaTypeId == MediaType.Series
                    ? await Context.ExecuteBatchAsync((ctx, ct) => AniListMatcher.ReadSeasonsAsync(ctx, target.WorkId, ct), CancellationToken)
                    : [], chosen, CancellationToken)
                : new IdentityMatchResultData(target.WorkId, AniListMetadata.Namespace, IdentityMatchStatusType.Dismissed, "[]", null, "[]", true);
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.SaveIdentityMatchAsync(result, ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
