using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Acquisition.V1
{
    // The release groups: by default not rated ones first, then good, bad and blocked, each by how often searches found them; a filter of rating and media type, a search and another order narrow it.
    [ServiceEndpoint("GET", "/api/v1/admin/acquisition/release-groups")]
    public sealed class ReadReleaseGroupsV1 : AdminService<ReadReleaseGroupsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<ReleaseGroupDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadReleaseGroupsParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ReadReleaseGroupsParametersV1 parameters, Type resultType) => new("acquisition.manage");
        protected override PageRequest GetPageRequest(ReadReleaseGroupsParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    item."Id",
                    item."Name",
                    item."ReleaseGroupRatingId",
                    item."SeenCount",
                    item."LastSeenAt",
                    coalesce(kinds."MediaKinds", '{}') AS "MediaKinds"
                FROM
                    "ReleaseGroups" AS item
                    LEFT JOIN LATERAL (
                        SELECT array_agg(seen."AcquisitionMediaKindId" ORDER BY seen."SeenCount" DESC, seen."AcquisitionMediaKindId") AS "MediaKinds"
                        FROM "ReleaseGroupMediaKinds" AS seen
                        WHERE seen."ReleaseGroupId" = item."Id"
                    ) AS kinds ON TRUE
                WHERE
                    (@Search::text IS NULL OR strpos(lower(item."Name"), lower(@Search::text)) > 0)
                    AND (@Filter::smallint IS NULL
                        OR (@Filter = 1 AND item."ReleaseGroupRatingId" IS NULL)
                        OR item."ReleaseGroupRatingId" = @Filter - 1)
                    AND (@MediaKind::smallint IS NULL
                        OR EXISTS (SELECT 1 FROM "ReleaseGroupMediaKinds" AS seen WHERE seen."ReleaseGroupId" = item."Id" AND seen."AcquisitionMediaKindId" = @MediaKind))
                ORDER BY
                    CASE WHEN @Sort = 1 THEN item."ReleaseGroupRatingId" END NULLS FIRST,
                    CASE WHEN @Sort = 2 THEN item."LastSeenAt" END DESC NULLS LAST,
                    CASE WHEN @Sort IN (1, 3) THEN item."SeenCount" END DESC,
                    CASE WHEN @Sort = 4 THEN lower(item."Name") END,
                    item."LastSeenAt" DESC NULLS LAST,
                    lower(item."Name"),
                    item."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Page = Page;
            sql.Parameters.Add(new ReleaseGroupFilterData(Parameters.Search, Parameters.Filter, Parameters.MediaKind, Parameters.Sort));
            return new PageResult<ReleaseGroupDetailsV1>(await Context.ReadSql.ReadListAsync<ReleaseGroupDetailsV1>(sql, CancellationToken), Page!);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/acquisition/release-groups/figures")]
    public sealed class ReadReleaseGroupFiguresV1 : AdminService<ReleaseGroupScopeParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ReleaseGroupFiguresV1>()];
        protected override ModuleRequirement GetInstanceModules(ReleaseGroupScopeParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ReleaseGroupScopeParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    count(*)::integer AS "GroupCount",
                    count(*) FILTER (WHERE item."ReleaseGroupRatingId" = 1)::integer AS "GoodCount",
                    count(*) FILTER (WHERE item."ReleaseGroupRatingId" IS NULL)::integer AS "NotRatedCount",
                    count(*) FILTER (WHERE item."ReleaseGroupRatingId" = 3)::integer AS "BlockedCount"
                FROM
                    "ReleaseGroups" AS item;
                """;
            return await Context.ReadSql.ReadRequiredAsync<ReleaseGroupFiguresV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/acquisition/release-groups")]
    public sealed class AddReleaseGroupV1 : AdminService<SaveAcquisitionPolicyParametersV1, AddReleaseGroupDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<SavedReleaseGroupV1>()];
        protected override ModuleRequirement GetInstanceModules(SaveAcquisitionPolicyParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(SaveAcquisitionPolicyParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnCreate()
        {
            var key = await Context.AddReleaseGroupAsync(new ReleaseGroupAddData(Data.Name), CancellationToken);
            return new SavedReleaseGroupV1(key.Id);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/acquisition/release-groups/{releaseGroupId:long}/rating")]
    public sealed class RateReleaseGroupV1 : AdminService<ReleaseGroupParametersV1, RateReleaseGroupDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ReleaseGroupParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ReleaseGroupParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.RateReleaseGroupAsync(new ReleaseGroupRatingData(Parameters.ReleaseGroupId, Data.ReleaseGroupRatingId), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
