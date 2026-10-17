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
    [ServiceEndpoint("GET", "/api/v1/admin/acquisition/release-profiles")]
    public sealed class ReadReleaseProfilesV1 : AdminService<ReadAcquisitionPolicyParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<ReleaseProfileDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadAcquisitionPolicyParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ReadAcquisitionPolicyParametersV1 parameters, Type resultType) => new("acquisition.manage");
        protected override PageRequest GetPageRequest(ReadAcquisitionPolicyParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    item."Id",
                    item."MediaTypeId",
                    item."Name",
                    item."IsEnabled",
                    item."Required",
                    item."Ignored",
                    item."AcquisitionConnectionId",
                    item."TagIds",
                    item."Version"
                FROM
                    "ReleaseProfiles" AS item
                WHERE
                    item."MediaTypeId" IS NULL OR item."MediaTypeId" = @MediaTypeId
                ORDER BY
                    item."Name",
                    item."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Page = Page;
            sql.Parameters.Add(new AcquisitionMediaScopeData(Parameters.MediaTypeId));
            return new PageResult<ReleaseProfileDetailsV1>(await Context.ReadSql.ReadListAsync<ReleaseProfileDetailsV1>(sql, CancellationToken), Page!);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/acquisition/release-profiles/{releaseProfileId:long}")]
    public sealed class ReadReleaseProfileV1 : AdminService<ReleaseProfileParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ReleaseProfileDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(ReleaseProfileParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ReleaseProfileParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    item."Id",
                    item."MediaTypeId",
                    item."Name",
                    item."IsEnabled",
                    item."Required",
                    item."Ignored",
                    item."AcquisitionConnectionId",
                    item."TagIds",
                    item."Version"
                FROM
                    "ReleaseProfiles" AS item
                WHERE
                    item."Id" = @ReleaseProfileId;
                """;
            sql.Parameters.Add(Parameters);
            return await Context.ReadSql.ReadRequiredAsync<ReleaseProfileDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/acquisition/release-profiles")]
    public sealed class SaveReleaseProfileV1 : AdminService<SaveAcquisitionPolicyParametersV1, SaveReleaseProfileDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<SavedAcquisitionPolicyV1>()];
        protected override ModuleRequirement GetInstanceModules(SaveAcquisitionPolicyParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(SaveAcquisitionPolicyParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            var key = await Context.SaveReleaseProfileAsync(new ReleaseProfileWriteData(Data.Id, Data.MediaTypeId, Data.Name, Data.IsEnabled,
                Data.Required, Data.Ignored, Data.AcquisitionConnectionId, Data.TagIds, Data.Version), CancellationToken);
            return new SavedAcquisitionPolicyV1(key.Id, key.Version);
        }
    }

    [ServiceEndpoint("DELETE", "/api/v1/admin/acquisition/release-profiles/{releaseProfileId:long}")]
    public sealed class DeleteReleaseProfileV1 : AdminService<DeleteReleaseProfileParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(DeleteReleaseProfileParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(DeleteReleaseProfileParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnDelete()
        {
            await Context.DeleteReleaseProfileAsync(new ReleaseProfileKeyData(Parameters.ReleaseProfileId, Parameters.Version), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/acquisition/tags")]
    public sealed class ReadAcquisitionTagsV1 : AdminService<ReadAcquisitionTagsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<AcquisitionTagDetailsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadAcquisitionTagsParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(ReadAcquisitionTagsParametersV1 parameters, Type resultType) => new("wanted.read");
        protected override PageRequest GetPageRequest(ReadAcquisitionTagsParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    item."Id",
                    item."Label",
                    item."Version"
                FROM
                    "Tags" AS item
                ORDER BY
                    item."Label",
                    item."Id"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Page = Page;

            return new PageResult<AcquisitionTagDetailsV1>(await Context.ReadSql.ReadListAsync<AcquisitionTagDetailsV1>(sql, CancellationToken), Page!);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/acquisition/tags/{acquisitionTagId:long}")]
    public sealed class ReadAcquisitionTagV1 : AdminService<AcquisitionTagParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AcquisitionTagDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionTagParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionTagParametersV1 parameters, Type resultType) => new("wanted.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    item."Id",
                    item."Label",
                    item."Version"
                FROM
                    "Tags" AS item
                WHERE
                    item."Id" = @AcquisitionTagId;
                """;
            sql.Parameters.Add(Parameters);
            return await Context.ReadSql.ReadRequiredAsync<AcquisitionTagDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/acquisition/tags")]
    public sealed class SaveAcquisitionTagV1 : AdminService<SaveAcquisitionPolicyParametersV1, SaveAcquisitionTagDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<SavedAcquisitionPolicyV1>()];
        protected override ModuleRequirement GetInstanceModules(SaveAcquisitionPolicyParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(SaveAcquisitionPolicyParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            var key = await Context.SaveAcquisitionTagAsync(new AcquisitionTagWriteData(Data.Id, Data.Label, Data.Version), CancellationToken);
            return new SavedAcquisitionPolicyV1(key.Id, key.Version);
        }
    }

    [ServiceEndpoint("DELETE", "/api/v1/admin/acquisition/tags/{acquisitionTagId:long}")]
    public sealed class DeleteAcquisitionTagV1 : AdminService<DeleteAcquisitionTagParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(DeleteAcquisitionTagParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(DeleteAcquisitionTagParametersV1 parameters, Type resultType) => new("acquisition.manage");

        protected override async Task<IServiceOutput> OnDelete()
        {
            await Context.DeleteAcquisitionTagAsync(new AcquisitionTagKeyData(Parameters.AcquisitionTagId, Parameters.Version), CancellationToken);
            return new UpdateResult(true);
        }
    }

}
