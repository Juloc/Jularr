using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Service.Admin.Wanted.V1;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.System.Acquisition.V1
{
    // Every 15 minutes: the recent feed of each indexer; a new release a wanted title can take is grabbed at once.
    public sealed class SyncFeedsV1 : SystemService<AcquisitionCycleParametersV1, NoData>
    {
        private readonly FeedSync _feed;

        internal SyncFeedsV1(FeedSync feed)
        {
            _feed = feed;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionCycleParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionCycleParametersV1 parameters, Type resultType) => new("system.acquisition");

        protected override async Task<IServiceOutput> OnExecute()
        {
            await _feed.RunAsync(Context, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
