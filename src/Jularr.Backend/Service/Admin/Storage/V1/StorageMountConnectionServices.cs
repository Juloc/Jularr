using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Storage.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Logic.Storage;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Nas;
using Jularr.Integrations.Logic.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Admin.Storage.V1
{
    [ServiceEndpoint("POST", "/api/v1/admin/storage/mounts/{storageMountId:long}/connection")]
    public sealed class ExecuteStorageMountV1 : AdminService<StorageMountParametersV1, ExecuteStorageMountDataV1>
    {
        private readonly NasMountClient _mounts;
        private readonly IDataProtector _protector;
        private readonly StorageOptions _options;

        internal ExecuteStorageMountV1(NasMountClient mounts, IDataProtectionProvider protection, IOptions<StorageOptions> options)
        {
            _mounts = mounts;
            _protector = protection.CreateProtector(StorageMountInput.ProtectorPurpose);
            _options = options.Value;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<StorageMountStatusV1>()];
        protected override ModuleRequirement GetInstanceModules(StorageMountParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(StorageMountParametersV1 parameters, Type resultType) => new("storage.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Read in an authorized batch.
            var mount = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadStorageMountAsync(Parameters.StorageMountId, ct), CancellationToken);

            // Local paths are present or absent.
            if (mount.StorageProtocolTypeId == StorageProtocolType.Local)
            {
                if (Data.OperationTypeId == StorageMountOperationType.Disconnect)
                {
                    throw new DataValidationException(nameof(ExecuteStorageMountDataV1.OperationTypeId), "invalid_transition");
                }

                var exists = Directory.Exists(_options.GetConnectionPath(mount.StorageProtocolTypeId, mount.Id, mount.SharePath));
                return new StorageMountStatusV1(exists, exists);
            }

            // Decrypt only for connection.
            string? password = null;
            var action = Data.OperationTypeId switch
            {
                StorageMountOperationType.Connect => NasMountAction.Connect,
                StorageMountOperationType.Disconnect => NasMountAction.Disconnect,
                _ => NasMountAction.Status
            };
            if (action == NasMountAction.Connect && mount.ProtectedPassword is { } protectedPassword)
            {
                byte[] clear;
                try
                {
                    clear = _protector.Unprotect(protectedPassword);
                }
                catch (CryptographicException)
                {
                    throw new NasMountException("storage_unavailable");
                }

                try
                {
                    password = Encoding.UTF8.GetString(clear);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(clear);
                }
            }

            // Keep NAS I/O outside transactions.
            var request = new NasMountRequest(action, mount.Id, mount.Version, mount.StorageProtocolTypeId.ToString(), mount.Host!, mount.SharePath,
                mount.ProtocolVersion!, mount.Username, mount.Domain, password, mount.IsReadOnly);
            var status = await _mounts.ExecuteAsync(request, CancellationToken);

            // Resolve concurrent configuration edits.
            var current = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadStorageMountAsync(Parameters.StorageMountId, ct), CancellationToken);
            return new StorageMountStatusV1(status.IsMounted, status.IsMounted && status.MountedVersion == current.Version);
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
        private readonly NasMountClient _mounts;

        internal RemoveStorageMountV1(NasMountClient mounts)
        {
            _mounts = mounts;
        }

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

            // Release helper mounts before forgetting them.
            if (mount.StorageProtocolTypeId != StorageProtocolType.Local && _mounts.IsConfigured)
            {
                await _mounts.ExecuteAsync(new NasMountRequest(NasMountAction.Disconnect, mount.Id, mount.Version, mount.StorageProtocolTypeId.ToString(), mount.Host!, mount.SharePath,
                    mount.ProtocolVersion!, mount.Username, mount.Domain, null, mount.IsReadOnly), CancellationToken);
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
