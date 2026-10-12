using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Service.Admin.Provider;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.System.Provider.V1
{
    public sealed class SyncPlexLibraryV1 : SystemService<AcquisitionCycleParametersV1, NoData>
    {
        private readonly PlexServer _plex;

        internal SyncPlexLibraryV1(PlexServer plex)
        {
            _plex = plex;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionCycleParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(AcquisitionCycleParametersV1 parameters, Type resultType) => new("system.acquisition");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Nothing to do without a connected server.
            if (await _plex.ConnectAsync(Context, CancellationToken) is { } connection)
            {
                await _plex.SyncAsync(Context, connection, CancellationToken);
            }

            return new UpdateResult(true);
        }
    }
}
