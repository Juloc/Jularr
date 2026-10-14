using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Session;
using Jularr.Backend.Data.User.Device.V1;
using Jularr.Backend.Logic.Session;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Device.V1
{
    [ServiceEndpoint("PUT", "/api/v1/devices/approvals")]
    public sealed class ApproveDevicePairingV1 : UserService<ApproveDevicePairingParametersV1, ApproveDevicePairingDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ApproveDevicePairingParametersV1 parameters) => ModuleRequirement.All("Devices");
        protected override ServicePermission GetPermission(ApproveDevicePairingParametersV1 parameters, Type resultType) => new("profile.devices");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            // The device signs in to the confirming account with its active profile.
            await Context.ApproveDevicePairingAsync(new DevicePairingApprovalData(Data.Code, Auth.AccountId!.Value, Auth.ProfileId!.Value), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
