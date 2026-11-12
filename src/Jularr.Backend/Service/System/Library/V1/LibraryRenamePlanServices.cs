using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.System.Library.V1;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.System.Library.V1
{
    // The Rename tab reads the kept plan of all libraries; the worker builds it again when it is due (see IsLibraryRenamePlanDueAsync).
    // The files are read and planned in a read-only batch, and only the result is written.
    public sealed class RefreshLibraryRenamePlanV1 : SystemService<LibraryRenamePlanParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(LibraryRenamePlanParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(LibraryRenamePlanParametersV1 parameters, Type resultType) => new("system.storage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            if (!await Context.ExecuteBatchAsync((ctx, ct) => ctx.IsLibraryRenamePlanDueAsync(ct), CancellationToken, mode: SqlAccessMode.ReadOnly))
            {
                return new UpdateResult(false);
            }

            var plan = await Context.ExecuteBatchAsync((ctx, ct) => ctx.PlanLibraryRenamesAsync(ct), CancellationToken, mode: SqlAccessMode.ReadOnly);
            return new UpdateResult(await Context.ExecuteBatchAsync((ctx, ct) => ctx.SaveLibraryRenamePlanAsync(plan, ct), CancellationToken));
        }
    }
}
