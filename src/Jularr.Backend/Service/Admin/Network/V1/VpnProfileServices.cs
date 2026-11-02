using System.Text;
using Jularr.Backend.Data.Admin.Network.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Logic.Network;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Network;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Network;

namespace Jularr.Backend.Service.Admin.Network.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/network/vpn/profiles")]
    public sealed class ReadVpnProfilesV1 : AdminService<ReadVpnProfilesParametersV1>
    {
        private readonly VpnTunnels _tunnels;

        internal ReadVpnProfilesV1(VpnTunnels tunnels)
        {
            _tunnels = tunnels;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<VpnProfileV1>>()];
        protected override ModuleRequirement GetInstanceModules(ReadVpnProfilesParametersV1 parameters) => ModuleRequirement.All("Vpn");
        protected override ServicePermission GetPermission(ReadVpnProfilesParametersV1 parameters, Type resultType) => new("instance.settings.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // At most a handful of profiles; their tunnels are read from memory, never assumed.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    profile."Id",
                    profile."Name",
                    profile."EndpointHost",
                    profile."EndpointPort",
                    profile."TunnelAddresses",
                    profile."DnsServers",
                    profile."HasIpv6Route",
                    profile."Version",
                    profile."CreatedAt"
                FROM
                    "VpnProfiles" AS profile
                ORDER BY
                    profile."Name",
                    profile."Id"
                LIMIT 100;
                """;
            var profiles = await Context.ReadSql.ReadListAsync<VpnProfileV1>(sql, CancellationToken);
            foreach (var profile in profiles) profile.Tunnel = _tunnels.Read(profile.Id);
            return new BaseDataList<VpnProfileV1>(profiles);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/network/vpn/profiles")]
    public sealed class ImportVpnProfileV1 : AdminService<ImportVpnProfileParametersV1, ImportVpnProfileDataV1>
    {
        private readonly VpnTunnels _tunnels;

        internal ImportVpnProfileV1(VpnTunnels tunnels)
        {
            _tunnels = tunnels;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CreateVpnProfileResultV1>()];
        protected override ModuleRequirement GetInstanceModules(ImportVpnProfileParametersV1 parameters) => ModuleRequirement.All("Vpn");
        protected override ServicePermission GetPermission(ImportVpnProfileParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnCreate()
        {
            var configuration = Data.Configuration;
            if (Data.File is { } file)
            {
                using var reader = new StreamReader(file.OpenRead(), Encoding.UTF8);
                configuration = await reader.ReadToEndAsync(CancellationToken);
            }

            if (string.IsNullOrWhiteSpace(configuration)) throw new DataValidationException(nameof(Data.Configuration), "required");
            var preview = VpnProfileLogic.Validate(configuration);
            var data = new VpnProfileSaveData(Data.Name, _tunnels.Protect(configuration), preview.EndpointHost, preview.EndpointPort, preview.TunnelAddresses,
                preview.DnsServers, preview.HasIpv6DefaultRoute);
            return new CreateVpnProfileResultV1(await Context.CreateVpnProfileAsync(data, CancellationToken));
        }
    }

    [ServiceEndpoint("DELETE", "/api/v1/admin/network/vpn/profiles/{vpnProfileId:long}")]
    public sealed class DeleteVpnProfileV1 : AdminService<DeleteVpnProfileParametersV1, NoData>
    {
        private readonly VpnTunnels _tunnels;

        internal DeleteVpnProfileV1(VpnTunnels tunnels)
        {
            _tunnels = tunnels;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(DeleteVpnProfileParametersV1 parameters) => ModuleRequirement.All("Vpn");
        protected override ServicePermission GetPermission(DeleteVpnProfileParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnDelete()
        {
            await Context.DeleteVpnProfileAsync(new VpnProfileKeyData(Parameters.VpnProfileId, Parameters.Version), CancellationToken);
            return new UpdateResult(true);
        }

        // The tunnel of a deleted profile ends with it.
        protected override void OnCommitted() => _ = _tunnels.StopAsync(Parameters.VpnProfileId);
    }

    // Starts the profile's tunnel when it does not run and measures it now: pings through it, the exit address against the host's own.
    [ServiceEndpoint("POST", "/api/v1/admin/network/vpn/profiles/{vpnProfileId:long}/check")]
    public sealed class CheckVpnProfileV1 : AdminService<VpnProfileParametersV1, NoData>
    {
        private readonly VpnTunnels _tunnels;

        internal CheckVpnProfileV1(VpnTunnels tunnels)
        {
            _tunnels = tunnels;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<VpnTunnelV1>()];
        protected override ModuleRequirement GetInstanceModules(VpnProfileParametersV1 parameters) => ModuleRequirement.All("Vpn");
        protected override ServicePermission GetPermission(VpnProfileParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var secret = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadVpnProfileSecretAsync(Parameters.VpnProfileId, ct), CancellationToken, SqlAccessMode.ReadOnly);
            try
            {
                return await _tunnels.CheckAsync(secret.Id, secret.ProtectedConfiguration, CancellationToken);
            }
            catch (TunnelUnavailableException exception)
            {
                throw new ServiceUnavailableException(exception.ReasonKey);
            }
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/network/vpn/profiles/{vpnProfileId:long}/stop")]
    public sealed class StopVpnProfileV1 : AdminService<VpnProfileParametersV1, NoData>
    {
        private readonly VpnTunnels _tunnels;

        internal StopVpnProfileV1(VpnTunnels tunnels)
        {
            _tunnels = tunnels;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(VpnProfileParametersV1 parameters) => ModuleRequirement.All("Vpn");
        protected override ServicePermission GetPermission(VpnProfileParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            await _tunnels.StopAsync(Parameters.VpnProfileId);
            return new UpdateResult(true);
        }
    }
}
