using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Session;
using Jularr.Backend.Data.Public.Device.V1;
using Jularr.Backend.Logic.Session;
using Jularr.Backend.Service.Core;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Public.Device.V1
{
    [ServiceEndpoint("POST", "/api/v1/devices/pairings")]
    public sealed class StartDevicePairingV1 : PublicService<StartDevicePairingParametersV1, StartDevicePairingDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<DevicePairingStartedV1>()];
        protected override ModuleRequirement GetInstanceModules(StartDevicePairingParametersV1 parameters) => ModuleRequirement.All("Devices");

        protected override async Task<IServiceOutput> OnCreate()
        {
            var started = await Context.StartDevicePairingAsync(Data.DeviceName, Data.DevicePlatform, Data.ReplacesPairingId, CancellationToken);
            return new DevicePairingStartedV1(started.PublicId, started.Code, started.ExpiresAt);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/devices/pairings/{pairingId:guid}/claim")]
    public sealed class ClaimDevicePairingV1 : PublicService<ClaimDevicePairingParametersV1, NoData>
    {
        private readonly AuthenticationOptions _options;

        internal ClaimDevicePairingV1(IOptions<AuthenticationOptions> options)
        {
            _options = options.Value;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<DevicePairingClaimV1>()];
        protected override ModuleRequirement GetInstanceModules(ClaimDevicePairingParametersV1 parameters) => ModuleRequirement.All("Devices");

        protected override async Task<IServiceOutput> OnCreate()
        {
            var session = await Context.ClaimDevicePairingAsync(new DevicePairingParameters(Parameters.PairingId), TimeSpan.FromHours(_options.SessionLifetimeHours), CancellationToken);
            return new DevicePairingClaimV1(session is not null, session);
        }
    }
}
