using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.System.Media.V1
{
    // Fetches the covers still named by a provider (titles that existed before covers were kept, a provider that was down) and the season and episode images; true while more are waiting.
    public sealed class StoreCoversV1 : SystemService<CoverParametersV1, NoData>
    {
        private const int PassSize = 20;
        private readonly CoverStore _covers;

        internal StoreCoversV1(CoverStore covers)
        {
            _covers = covers;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(CoverParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(CoverParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            if (!_covers.IsEnabled)
            {
                return new UpdateResult(false);
            }

            var targets = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadPendingCoversAsync(new WorkCoverQueryData(null, PassSize), ct), CancellationToken);
            if (targets.Count == 0)
            {
                return new UpdateResult(false);
            }

            await _covers.StoreAsync(Context, targets, CancellationToken);
            return new UpdateResult(targets.Count == PassSize);
        }
    }

    // Removes cover files no stored cover refers to, such as those of deleted Works, and the cached images of titles that are not Works nobody uses any more.
    public sealed class CleanCoversV1 : SystemService<CoverParametersV1, NoData>
    {
        private readonly CoverStore _covers;

        internal CleanCoversV1(CoverStore covers)
        {
            _covers = covers;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(CoverParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(CoverParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // The cache of titles that are not Works keeps to the Owner's days and size.
            var settings = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadCoverCacheSettingsAsync(ct), CancellationToken);
            var trimmed = _covers.TrimExternal(settings.KeepDays, settings.MaximumMegabytes * 1024L * 1024L);
            return new UpdateResult(await _covers.SweepAsync(Context, CancellationToken) + trimmed > 0);
        }
    }
}
