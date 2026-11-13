using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.System.Acquisition.V1
{
    // The tags Jularr keeps itself (media type, original language, genres, requesters) brought in line with the titles; release profiles may ask for them.
    public sealed class RefreshAutoTagsV1 : SystemService<AutoTagParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AutoTagParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AutoTagParametersV1 parameters, Type resultType) => new("system.acquisition");

        protected override async Task<IServiceOutput> OnExecute() =>
            new UpdateResult(await Context.ExecuteBatchAsync((ctx, ct) => ctx.RefreshAutoTagsAsync(ct), CancellationToken) > 0);
    }
}
