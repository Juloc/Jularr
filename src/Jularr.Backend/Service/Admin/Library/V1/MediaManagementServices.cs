using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Library.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/library/media-management")]
    public sealed class ReadMediaManagementSettingsV1 : AdminService<ReadMediaManagementParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<MediaManagementSettingsV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadMediaManagementParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReadMediaManagementParametersV1 parameters, Type resultType) => new("storage.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    settings."MediaTypeId",
                    settings."UsesHardlinks",
                    settings."SkipsFreeSpaceCheck",
                    settings."MinimumFreeSpaceMegabytes",
                    settings."AnalysesMedia",
                    settings."Version"
                FROM "MediaManagementSettings" AS settings
                ORDER BY settings."MediaTypeId";
                """;
            return new BaseDataList<MediaManagementSettingsV1>([.. await Context.ReadListAsync<MediaManagementSettingsV1>(sql, CancellationToken)]);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/library/media-management/{mediaTypeId}")]
    public sealed class UpdateMediaManagementSettingsV1 : AdminService<MediaManagementParametersV1, UpdateMediaManagementDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(MediaManagementParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(MediaManagementParametersV1 parameters, Type resultType) => new("storage.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.SaveMediaManagementSettingsAsync(new MediaManagementSettingsData(Parameters.MediaTypeId, Data.UsesHardlinks, Data.SkipsFreeSpaceCheck,
                Data.MinimumFreeSpaceMegabytes, Data.AnalysesMedia, Data.Version), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
