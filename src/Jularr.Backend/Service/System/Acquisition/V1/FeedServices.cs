using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Service.Admin.Wanted.V1;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.System.Acquisition.V1
{
    // Every 15 minutes: the recent feed of each indexer, and the wanted targets it makes due are searched at once.
    public sealed class SyncFeedsV1 : SystemService<AcquisitionCycleParametersV1, NoData>
    {
        private readonly FeedSync _feed;
        private readonly AcquisitionSearchSignal _searchSignal;

        internal SyncFeedsV1(FeedSync feed, AcquisitionSearchSignal searchSignal)
        {
            _feed = feed;
            _searchSignal = searchSignal;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionCycleParametersV1 parameters) => ModuleRequirement.All("Acquisition");
        protected override ServicePermission GetPermission(AcquisitionCycleParametersV1 parameters, Type resultType) => new("system.acquisition");

        protected override async Task<IServiceOutput> OnExecute()
        {
            if (await _feed.RunAsync(Context, CancellationToken) > 0)
            {
                _searchSignal.RequestSearch();
            }

            return new UpdateResult(true);
        }
    }
}
