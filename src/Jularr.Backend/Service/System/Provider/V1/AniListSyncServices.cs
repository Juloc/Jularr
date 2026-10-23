using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.System.Provider.V1;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.System.Provider.V1
{
    // Every connected profile's AniList list is read, finished titles are taken over and progress made since sending was switched on is sent.
    // A pass an AniList limit ends reports no work; an unreachable AniList fails it.
    public sealed class SyncAniListV1 : SystemService<AniListSyncParametersV1, NoData>
    {
        private readonly AniListAccounts _accounts;

        internal SyncAniListV1(AniListAccounts accounts)
        {
            _accounts = accounts;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AniListSyncParametersV1 parameters) => ModuleRequirement.All("ProgressSync", "Library", "AniList");
        protected override ServicePermission GetPermission(AniListSyncParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute() => new UpdateResult(await _accounts.SyncAsync(Context, CancellationToken));
    }
}
