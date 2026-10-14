using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Session;
using Jularr.Backend.Data.User.Device.V1;
using Jularr.Backend.Logic.Session;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Device.V1
{
    // The person confirming first sees which device asks; looking up a code counts as trying it.
    [ServiceEndpoint("POST", "/api/v1/devices/approvals/inspection", RateLimit = ServiceApi.DeviceApprovalRateLimit)]
    public sealed class InspectDevicePairingV1 : UserService<ApproveDevicePairingParametersV1, ApproveDevicePairingDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<DevicePairingRequestV1>()];
        protected override ModuleRequirement GetInstanceModules(ApproveDevicePairingParametersV1 parameters) => ModuleRequirement.All("Devices");
        protected override ServicePermission GetPermission(ApproveDevicePairingParametersV1 parameters, Type resultType) => new("profile.devices");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var account = new DevicePairingAccountData(Auth.AccountId!.Value);
            var attempt = await Context.ExecuteBatchAsync((ctx, ct) => ctx.TakeDevicePairingAttemptAsync(account, ct), CancellationToken);
            var device = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadOpenDevicePairingAsync(new DevicePairingCodeData(Data.Code), ct), CancellationToken)
                ?? throw new DataValidationException(nameof(ApproveDevicePairingDataV1.Code), "invalid_code");
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.ReturnDevicePairingAttemptAsync(attempt, ct);
                return true;
            }, CancellationToken);
            return new DevicePairingRequestV1(device.DeviceName, device.DevicePlatform, Data.Code);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/devices/approvals", RateLimit = ServiceApi.DeviceApprovalRateLimit)]
    public sealed class ApproveDevicePairingV1 : UserService<ApproveDevicePairingParametersV1, ApproveDevicePairingDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(ApproveDevicePairingParametersV1 parameters) => ModuleRequirement.All("Devices");
        protected override ServicePermission GetPermission(ApproveDevicePairingParametersV1 parameters, Type resultType) => new("profile.devices");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // The device signs in to the confirming account with its active profile.
            var account = new DevicePairingAccountData(Auth.AccountId!.Value);
            var attempt = await Context.ExecuteBatchAsync((ctx, ct) => ctx.TakeDevicePairingAttemptAsync(account, ct), CancellationToken);
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.ApproveDevicePairingAsync(new DevicePairingApprovalData(Data.Code, account.AccountId, Auth.ProfileId!.Value), ct);
                await ctx.ReturnDevicePairingAttemptAsync(attempt, ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
