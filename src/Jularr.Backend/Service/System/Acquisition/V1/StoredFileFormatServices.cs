using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.System.Acquisition.V1
{
    public sealed class RefreshStoredFileFormatsV1 : SystemService<AcquisitionCycleParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ServicePermission GetPermission(AcquisitionCycleParametersV1 parameters, Type resultType) => new("system.acquisition");
        protected override ModuleRequirement GetInstanceModules(AcquisitionCycleParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override async Task<IServiceOutput> OnExecute()
        {
            var count = await Context.ExecuteBatchAsync((ctx, ct) => ctx.RefreshStoredFileFormatsAsync(ct), CancellationToken);
            return new UpdateResult(count > 0);
        }
    }
}
