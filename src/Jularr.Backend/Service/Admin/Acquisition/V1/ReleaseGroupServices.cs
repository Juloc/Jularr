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
    // A media type's release groups: not rated ones first, then good, bad and blocked, each by how often searches found them.
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
                    item."MediaTypeId",
                    item."Name",
                    item."ReleaseGroupRatingId",
                    item."SeenCount",
                    item."LastSeenAt"
                FROM
                    "ReleaseGroups" AS item
                WHERE
                    item."MediaTypeId" = @MediaTypeId
                    AND (@Search::text IS NULL OR strpos(lower(item."Name"), lower(@Search::text)) > 0)
                ORDER BY
                    item."ReleaseGroupRatingId" NULLS FIRST,
                    item."SeenCount" DESC,
                    item."LastSeenAt" DESC NULLS LAST,
                    lower(item."Name"),
                    item."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Page = Page;
            sql.Parameters.Add(new ReleaseGroupFilterData(Parameters.MediaTypeId, Parameters.Search));
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
                    "ReleaseGroups" AS item
                WHERE
                    item."MediaTypeId" = @MediaTypeId;
                """;
            sql.Parameters.Add(Parameters);
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
            var key = await Context.AddReleaseGroupAsync(new ReleaseGroupAddData(Data.MediaTypeId, Data.Name), CancellationToken);
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
