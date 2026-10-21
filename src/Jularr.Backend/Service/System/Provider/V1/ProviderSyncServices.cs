using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Provider;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.System.Provider.V1;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.System.Provider.V1
{
    // One pass over one connected server: Plex adds its titles to the library, Jellyfin and Emby exchange watch progress.
    // The unreachable one fails alone; a server that is not connected has nothing to do and reports no work.
    public sealed class SyncProviderV1 : SystemService<ProviderSyncParametersV1, NoData>
    {
        private readonly PlexServer _plex;
        private readonly MediaServers _servers;

        internal SyncProviderV1(PlexServer plex, MediaServers servers)
        {
            _plex = plex;
            _servers = servers;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];

        // Plex needs no module; the media servers share watch progress with the library.
        protected override ModuleRequirement GetInstanceModules(ProviderSyncParametersV1 parameters) =>
            parameters.ProviderSyncTypeId == ProviderSyncType.Plex ? ModuleRequirement.None : ModuleRequirement.All("ProgressSync", "Library");

        protected override ServicePermission GetPermission(ProviderSyncParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            if (Parameters.ProviderSyncTypeId == ProviderSyncType.Plex)
            {
                if (await _plex.ConnectAsync(Context, CancellationToken) is not { } plex)
                {
                    return new UpdateResult(false);
                }

                await _plex.SyncAsync(Context, plex, CancellationToken);
                return new UpdateResult(true);
            }

            var type = Parameters.ProviderSyncTypeId == ProviderSyncType.Jellyfin ? MediaServerType.Jellyfin : MediaServerType.Emby;
            if (await _servers.ConnectAsync(Context, type, CancellationToken) is not { } connection)
            {
                return new UpdateResult(false);
            }

            await _servers.SyncAsync(Context, type, connection, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
