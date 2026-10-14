using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Notification;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Logic.Notification;
using Jularr.Backend.Data.Logic.Storage;
using Jularr.Backend.Data.System.Storage.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Notification;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Nas;
using Jularr.Integrations.Logic.Nas;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.System.Storage.V1
{
    // Remounts lost connections the Owner connected.
    public sealed class ReconnectStorageMountsV1 : SystemService<StorageConnectionsParametersV1, NoData>
    {
        private readonly NasMountClient _mounts;
        private readonly IDataProtector _protector;
        private readonly StorageOptions _options;

        internal ReconnectStorageMountsV1(NasMountClient mounts, IDataProtectionProvider protection, IOptions<StorageOptions> options)
        {
            _mounts = mounts;
            _protector = protection.CreateProtector(StorageMountRequests.ProtectorPurpose);
            _options = options.Value;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(StorageConnectionsParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(StorageConnectionsParametersV1 parameters, Type resultType) => new("system.storage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            if (!_mounts.IsConfigured)
            {
                return new UpdateResult(false);
            }

            var due = await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        state."StorageMountId",
                        state."ReconnectAttempts"
                    FROM
                        "StorageMountStates" AS state
                        JOIN "StorageMounts" AS mount ON mount."Id" = state."StorageMountId"
                    WHERE
                        state."IsConnectRequested"
                        AND mount."StorageProtocolTypeId" <> 3
                        AND (state."ReconnectAt" IS NULL OR state."ReconnectAt" <= now())
                    ORDER BY
                        state."StorageMountId"
                    LIMIT 20;
                    """;
                return ctx.ReadSql.ReadListAsync<StorageReconnectCandidateData>(sql, ct);
            }, CancellationToken);

            foreach (var candidate in due)
            {
                StorageMountData mount;
                try
                {
                    mount = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadStorageMountAsync(candidate.StorageMountId, ct), CancellationToken);
                }
                catch (KeyNotFoundException)
                {
                    // Removed meanwhile.
                    continue;
                }

                StorageMountProbeData probe;
                try
                {
                    // Mounted shares stay untouched.
                    var status = await _mounts.ExecuteAsync(StorageMountRequests.Create(mount, NasMountAction.Status, _protector), CancellationToken);
                    if (status.IsMounted && candidate.ReconnectAttempts == 0)
                    {
                        continue;
                    }

                    if (!status.IsMounted)
                    {
                        await _mounts.ExecuteAsync(StorageMountRequests.Create(mount, NasMountAction.Connect, _protector), CancellationToken);
                    }

                    probe = await _options.ProbeAsync(mount.Id, mount.StorageProtocolTypeId, mount.SharePath, CancellationToken);
                }
                catch (NasMountException exception) when (exception.Code == "storage_unavailable")
                {
                    probe = new StorageMountProbeData(mount.Id, StorageAvailabilityType.Offline, exception.Code, null, null);
                }
                catch (NasMountException exception) when (exception.Code == "storage_helper_unavailable")
                {
                    // The helper itself is down.
                    return new UpdateResult(false);
                }
                catch (NasMountException)
                {
                    // Busy or reconfigured meanwhile.
                    continue;
                }

                await Context.ExecuteBatchAsync(async (ctx, ct) =>
                {
                    var wanted = await ctx.SaveStorageReconnectAsync(probe, candidate.ReconnectAttempts + 1, ct);

                    // The Owner hears once the third attempt failed.
                    if (wanted && probe.StorageAvailabilityTypeId != StorageAvailabilityType.Available && candidate.ReconnectAttempts + 1 == 3)
                    {
                        await ctx.NotifyStaffAsync(new StaffNotificationData(NotificationType.StorageOffline, null, mount.Name, "/Admin/Storage?storageMountId=" + mount.Id, true), ct);
                    }

                    return true;
                }, CancellationToken);
            }

            return new UpdateResult(true);
        }
    }
}
