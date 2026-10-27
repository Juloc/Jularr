using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/catalog/cache")]
    public sealed class ReadCatalogCacheV1 : AdminService<ReadCatalogCacheParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CatalogCacheV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadCatalogCacheParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ReadCatalogCacheParametersV1 parameters, Type resultType) => new("instance.settings.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var providers = await Context.ReadCatalogCacheProvidersAsync(CancellationToken);
            var keep = await Context.ReadCatalogKeepDaysAsync(CancellationToken);
            return new CatalogCacheV1(keep.KeepDays, new BaseDataList<CatalogCacheProviderV1>([.. providers.Select(provider =>
                new CatalogCacheProviderV1(provider.Namespace, provider.CacheTier, ExternalWorkLogic.MaximumCacheTier(provider.Namespace), provider.DetailsTrending))]));
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
            await Context.UpdateCatalogCacheAsync([.. Data.Providers.Select(provider => new CatalogCacheProviderData(provider.Namespace, provider.CacheTier, provider.DetailsTrending))],
                new CatalogKeepDaysData(Data.KeepDays), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
