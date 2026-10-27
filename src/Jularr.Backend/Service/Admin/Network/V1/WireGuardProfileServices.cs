using Jularr.Backend.Data.Admin.Network.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Logic.Network;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Network.V1
{
    [ServiceEndpoint("POST", "/api/v1/admin/network/wireguard/preview")]
    public sealed class PreviewWireGuardProfileV1 : AdminService<WireGuardPreviewParametersV1, PreviewWireGuardDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<WireGuardPreviewV1>()];
        protected override ModuleRequirement GetInstanceModules(WireGuardPreviewParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(WireGuardPreviewParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override Task<IServiceOutput> OnExecute()
        {
            WireGuardProfilePreview preview;
            try
            {
                preview = WireGuardProfileLogic.Preview(Data.Configuration);
            }
            catch (ArgumentException)
            {
                // Never reflect submitted config or private keys into responses, exceptions or logs.
                throw new DataValidationException(nameof(Data.Configuration), "invalid_wireguard_configuration");
            }

            return Task.FromResult<IServiceOutput>(new WireGuardPreviewV1(preview.EndpointHost, preview.EndpointPort,
                preview.TunnelAddresses, preview.DnsServers, preview.HasIpv6DefaultRoute, preview.HasPresharedKey));
        }
    }
}
