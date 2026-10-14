using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Provider;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.System.Provider.V1;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.System.Provider.V1
{
    public sealed class SyncMediaServersV1 : SystemService<MediaServerSyncParametersV1, NoData>
    {
        private readonly MediaServers _servers;

        internal SyncMediaServersV1(MediaServers servers)
        {
            _servers = servers;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(MediaServerSyncParametersV1 parameters) => ModuleRequirement.All("ProgressSync", "Library");
        protected override ServicePermission GetPermission(MediaServerSyncParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Each connected server in turn; one that cannot be reached does not hold up the other.
            ServiceUnavailableException? failure = null;
            foreach (var type in Enum.GetValues<MediaServerType>())
            {
                if (await _servers.ConnectAsync(Context, type, CancellationToken) is not { } connection)
                {
                    continue;
                }

                try
                {
                    await _servers.SyncAsync(Context, type, connection, CancellationToken);
                }
                catch (ServiceUnavailableException exception)
                {
                    failure ??= exception;
                }
            }

            return failure is null ? new UpdateResult(true) : throw failure;
        }
    }
}
