using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/catalog/covers")]
    public sealed class ReadCoverCacheV1 : AdminService<ReadCoverCacheParametersV1>
    {
        private readonly CoverStore _covers;

        internal ReadCoverCacheV1(CoverStore covers)
        {
            _covers = covers;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CoverCacheV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadCoverCacheParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReadCoverCacheParametersV1 parameters, Type resultType) => new("instance.settings.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var settings = await Context.ReadCoverCacheSettingsAsync(CancellationToken);
            return new CoverCacheV1(settings.KeepDays, settings.MaximumMegabytes, settings.FormatId, _covers.EffectiveFormat(settings.FormatId), settings.Version);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/catalog/covers")]
    public sealed class UpdateCoverCacheV1 : AdminService<ReadCoverCacheParametersV1, UpdateCoverCacheDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ReadCoverCacheParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReadCoverCacheParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.UpdateCoverCacheSettingsAsync(new CoverCacheSettingsData(Data.KeepDays, Data.MaximumMegabytes, Data.FormatId, Data.Version), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
