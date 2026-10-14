using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Session;
using Jularr.Backend.Data.User.Device.V1;
using Jularr.Backend.Logic.Session;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Device.V1
{
    [ServiceEndpoint("GET", "/api/v1/devices/approvals/{code}")]
    public sealed class ReadDevicePairingV1 : UserService<DevicePairingCodeParametersV1>
    {
        private readonly DevicePairingAttempts _attempts;

        internal ReadDevicePairingV1(DevicePairingAttempts attempts)
        {
            _attempts = attempts;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<DevicePairingRequestV1>()];
        protected override ModuleRequirement GetInstanceModules(DevicePairingCodeParametersV1 parameters) => ModuleRequirement.All("Devices");
        protected override ServicePermission GetPermission(DevicePairingCodeParametersV1 parameters, Type resultType) => new("profile.devices");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The person confirming sees which device asks; looking up codes counts as guessing them.
            var accountId = Auth.AccountId!.Value;
            _attempts.Take(accountId);
            var device = await Context.ReadOpenDevicePairingAsync(new DevicePairingCodeData(Parameters.Code), CancellationToken)
                ?? throw new DataValidationException(nameof(DevicePairingCodeParametersV1.Code), "invalid_code");
            _attempts.GiveBack(accountId);
            return new DevicePairingRequestV1(device.DeviceName, device.DevicePlatform);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/devices/approvals")]
    public sealed class ApproveDevicePairingV1 : UserService<ApproveDevicePairingParametersV1, ApproveDevicePairingDataV1>
    {
        private readonly DevicePairingAttempts _attempts;

        internal ApproveDevicePairingV1(DevicePairingAttempts attempts)
        {
            _attempts = attempts;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ApproveDevicePairingParametersV1 parameters) => ModuleRequirement.All("Devices");
        protected override ServicePermission GetPermission(ApproveDevicePairingParametersV1 parameters, Type resultType) => new("profile.devices");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            // The device signs in to the confirming account with its active profile.
            var accountId = Auth.AccountId!.Value;
            _attempts.Take(accountId);
            await Context.ApproveDevicePairingAsync(new DevicePairingApprovalData(Data.Code, accountId, Auth.ProfileId!.Value), CancellationToken);
            _attempts.GiveBack(accountId);
            return new UpdateResult(true);
        }
    }
}
