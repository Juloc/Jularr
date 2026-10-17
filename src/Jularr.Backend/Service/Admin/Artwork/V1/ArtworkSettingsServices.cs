using Jularr.Backend.Data.Admin.Artwork.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Artwork.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/artwork/settings")]
    public sealed class ReadArtworkSettingsV1 : AdminService<ArtworkSettingsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ArtworkSettingsDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(ArtworkSettingsParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ArtworkSettingsParametersV1 parameters, Type resultType) => new("instance.settings.read");

        protected override async Task<IServiceOutput> OnRead() =>
            await Context.ReadAdminArtworkSettingsAsync(CancellationToken);
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/artwork/settings")]
    public sealed class UpdateArtworkSettingsV1 : AdminService<ArtworkSettingsParametersV1, UpdateArtworkSettingsDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ArtworkSettingsParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ArtworkSettingsParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.SaveAdminArtworkSettingsAsync(Data, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
