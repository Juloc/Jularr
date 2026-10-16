using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Storage;
using Jularr.Backend.Data.System.Storage.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.System.Storage.V1
{
    // Looks again at mounts that failed once their pause is over.
    public sealed class CheckStorageMountsV1 : SystemService<StorageChecksParametersV1, NoData>
    {
        private readonly StorageAccess _storage;

        internal CheckStorageMountsV1(StorageAccess storage)
        {
            _storage = storage;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(StorageChecksParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(StorageChecksParametersV1 parameters, Type resultType) => new("system.storage");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var due = await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        state."StorageMountId"
                    FROM
                        "StorageMountStates" AS state
                    WHERE
                        state."FailedChecks" > 0
                        AND state."NextCheckAt" <= now()
                    ORDER BY
                        state."NextCheckAt"
                    LIMIT 20;
                    """;
                return ctx.ReadSql.ReadListAsync<StorageCheckCandidateData>(sql, ct);
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

                // Storage I/O stays outside transactions.
                var probe = await _storage.ProbeAsync(mount, CancellationToken);
                await Context.ExecuteBatchAsync(async (ctx, ct) =>
                {
                    await ctx.SaveStorageMountProbeAsync(probe, ct);
                    return true;
                }, CancellationToken);
            }

            return new UpdateResult(due.Count > 0);
        }
    }
}
