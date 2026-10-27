using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.System.Media.V1
{
    // Daily: the details of catalog titles nobody uses are dropped, then the catalog rows that providers without a whole catalog (cache tier 1 and 2) do not need.
    // Each statement changes at most BatchSize titles, so the Works table is never locked for long. The covers of a reset title are swept with the next cover cleanup.
    public sealed class CleanCatalogTitlesV1 : SystemService<CoverParametersV1, NoData>
    {
        private const int BatchSize = 1000;
        // A pass stops after this many batches; the next one continues.
        private const int MaximumBatches = 200;

        internal CleanCatalogTitlesV1()
        {
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(CoverParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(CoverParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var changed = 0;
            var batches = 0;
            var data = new CatalogCleanupData(BatchSize);
            int count;
            do
            {
                count = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ResetUnusedCatalogDetailsAsync(data, ct), CancellationToken);
                changed += count;
            }
            while (count == BatchSize && ++batches < MaximumBatches);

            do
            {
                count = await Context.ExecuteBatchAsync((ctx, ct) => ctx.DeleteUnusedCatalogTitlesAsync(data, ct), CancellationToken);
                changed += count;
            }
            while (count == BatchSize && ++batches < MaximumBatches);

            return new UpdateResult(changed > 0);
        }
    }
}
