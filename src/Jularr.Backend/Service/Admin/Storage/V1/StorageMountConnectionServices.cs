using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Storage;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Nas;

namespace Jularr.Backend.Service.Admin.Storage.V1
{
    // Looks at the mounted share or path now, as the background check does.
    [ServiceEndpoint("POST", "/api/v1/admin/storage/mounts/{storageMountId:long}/check")]
    public sealed class CheckStorageMountV1 : AdminService<StorageMountParametersV1, NoData>
    {
        private readonly StorageAccess _storage;

        internal CheckStorageMountV1(StorageAccess storage)
        {
            _storage = storage;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<StorageMountStatusV1>()];
        protected override ModuleRequirement GetInstanceModules(StorageMountParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(StorageMountParametersV1 parameters, Type resultType) => new("storage.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var mount = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadStorageMountAsync(Parameters.StorageMountId, ct), CancellationToken);

            // Storage I/O stays outside transactions.
            var probe = await _storage.ProbeAsync(mount, CancellationToken);
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.SaveStorageMountProbeAsync(probe, ct);
                return true;
            }, CancellationToken);
            return new StorageMountStatusV1(probe.StorageAvailabilityTypeId, probe.DiagnosticCode);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/storage/mounts/{storageMountId:long}/wake")]
    public sealed class WakeStorageMountV1 : AdminService<StorageMountParametersV1, NoData>
    {
        private readonly WakeOnLanClient _wake;

        internal WakeStorageMountV1(WakeOnLanClient wake)
        {
            _wake = wake;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(StorageMountParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(StorageMountParametersV1 parameters, Type resultType) => new("storage.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var mount = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadStorageMountAsync(Parameters.StorageMountId, ct), CancellationToken);
            if (mount.WakeMacAddress is not { } macAddress)
            {
                throw new DataValidationException(nameof(StorageMountDetailsV1.WakeMacAddress), "required");
            }

            // The Owner may always wake the device.
            await _wake.SendAsync(macAddress, mount.WakeBroadcastAddress, CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/storage/mounts/{storageMountId:long}/removal")]
    public sealed class RemoveStorageMountV1 : AdminService<StorageMountParametersV1, RemoveStorageMountDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(StorageMountParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(StorageMountParametersV1 parameters, Type resultType) => new("storage.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var mount = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadStorageMountAsync(Parameters.StorageMountId, ct), CancellationToken);
            if (mount.Version != Data.Version)
            {
                throw new DataValidationException(nameof(RemoveStorageMountDataV1.Version), "outdated_version");
            }

            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.DeleteStorageMountAsync(new StorageMountParameters(Parameters.StorageMountId, Data.Version), ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }
    }
}
