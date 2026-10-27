using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    // The catalog choices of the whole instance; each catalog's own tier is saved with its card (UpdateExternalWorkSyncV1).
    [ServiceEndpoint("GET", "/api/v1/admin/catalog/cache")]
    public sealed class ReadCatalogCacheV1 : AdminService<ReadCatalogCacheParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CatalogCacheV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadCatalogCacheParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReadCatalogCacheParametersV1 parameters, Type resultType) => new("instance.settings.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var settings = await Context.ReadCatalogCacheSettingsAsync(CancellationToken);
            return new CatalogCacheV1(settings.KeepDays, settings.DetailsDiscoverAhead);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/catalog/cache")]
    public sealed class UpdateCatalogCacheV1 : AdminService<ReadCatalogCacheParametersV1, UpdateCatalogCacheDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ReadCatalogCacheParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReadCatalogCacheParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.UpdateCatalogCacheSettingsAsync(new CatalogCacheSettingsData(Data.KeepDays, Data.DetailsDiscoverAhead), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
