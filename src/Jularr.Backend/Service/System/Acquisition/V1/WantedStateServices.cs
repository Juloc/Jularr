using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.System.Acquisition.V1
{
    // The Wanted list reads the states the worker keeps; it refreshes them once something they read changed or a new day began (see WantedStateLogic).
    public sealed class RefreshWantedStatesV1 : SystemService<WantedStateParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(WantedStateParametersV1 parameters) => ModuleRequirement.All("Requests");
        protected override ServicePermission GetPermission(WantedStateParametersV1 parameters, Type resultType) => new("system.acquisition");

        protected override async Task<IServiceOutput> OnExecute()
        {
            if (await Context.ExecuteBatchAsync((ctx, ct) => ctx.IsWantedStateCurrentAsync(ct), CancellationToken, mode: SqlAccessMode.ReadOnly))
            {
                return new UpdateResult(false);
            }

            await Context.ExecuteBatchAsync((ctx, ct) => ctx.RefreshWantedStatesAsync(ct), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
