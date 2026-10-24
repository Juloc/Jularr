using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Enums.Storage;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.System.Acquisition.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;
using Microsoft.Extensions.Logging;

namespace Jularr.Backend.Service.System.Acquisition.V1
{
    // Bounded periodic reconciliation of orphaned direct-source staging, never of unowned paths.
    public sealed class ReconcileDirectDownloadFoldersV1 : SystemService<AcquisitionCycleParametersV1, NoData>
    {
        private readonly DownloadStorage _storage;
        private readonly DirectDownloadCleanup _cleanup;
        private readonly ILogger<ReconcileDirectDownloadFoldersV1> _logger;

        internal ReconcileDirectDownloadFoldersV1(DownloadStorage storage, DirectDownloadCleanup cleanup, ILogger<ReconcileDirectDownloadFoldersV1> logger)
        {
            _storage = storage;
            _cleanup = cleanup;
            _logger = logger;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AcquisitionCycleParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(AcquisitionCycleParametersV1 parameters, Type resultType) => new("system.acquisition");

        protected override async Task<IServiceOutput> OnExecute()
        {
            DownloadStoragePaths paths;
            try
            {
                paths = await _storage.OpenAsync(Context, DownloadStorageArea.Direct, CancellationToken, skipCapacityCheck: true);
            }
            catch (ServiceUnavailableException)
            {
                // No configured workspace or detached mount: never delete by guessing a path.
                return new UpdateResult(false);
            }

            var now = DateTime.UtcNow;
            IReadOnlyList<DirectDownloadCleanup.Candidate> candidates;
            try
            {
                candidates = _cleanup.Candidates(paths, now);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Could not enumerate direct download cleanup candidates.");
                return new UpdateResult(false);
            }

            if (candidates.Count == 0) return new UpdateResult(false);
            // A successful authoritative database read is mandatory before deleting even one folder.
            var ids = candidates.Select(item => item.OperationId).Distinct().ToArray();
            var rows = await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        candidate."Id" AS "OperationId",
                        (operation."Id" IS NULL OR (operation."OperationTypeId" = 2 AND operation."OperationStatusTypeId" IN (3, 4)
                            AND connection."AcquisitionConnectionTypeId" IN (3, 4, 5))) AS "IsFinished",
                        operation."Id" IS NULL AS "IsMissing"
                    FROM unnest(@OperationIds::bigint[]) AS candidate("Id")
                    LEFT JOIN "Operations" AS operation ON operation."Id" = candidate."Id"
                    LEFT JOIN "AcquisitionConnections" AS connection ON connection."Id" = operation."AcquisitionConnectionId";
                    """;
                sql.Parameters.Add(new DownloadCleanupIdsData(ids));
                return await ctx.ReadSql.ReadListAsync<DownloadCleanupStateData>(sql, ct);
            }, CancellationToken);
            if (rows.Count != ids.Length) return new UpdateResult(false);
            var status = rows.ToDictionary(row => row.OperationId);
            var deleted = 0;
            var skipped = 0;
            var errors = 0;
            long reclaimedBytes = 0;
            foreach (var candidate in candidates)
            {
                CancellationToken.ThrowIfCancellationRequested();
                if (!status.TryGetValue(candidate.OperationId, out var state)
                    || !DirectDownloadCleanup.CanRemove(candidate, state.IsFinished, state.IsMissing, now))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    // Do not reap a job that another worker has already reserved.
                    using var lease = paths.TryStartJob(candidate.OperationId);
                    if (lease is null)
                    {
                        skipped++;
                        continue;
                    }

                    reclaimedBytes += DirectDownloadCleanup.Remove(paths, candidate, now, state.IsMissing);
                    deleted++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ServiceUnavailableException)
                {
                    skipped++;
                    errors++;
                    _logger.LogWarning(exception, "Direct download cleanup skipped operation {OperationId}.", candidate.OperationId);
                }
            }

            _cleanup.Report(candidates.Count, deleted, skipped, errors, reclaimedBytes);
            if (deleted > 0)
                _logger.LogInformation("Direct download workspace cleanup removed {Deleted} stale directories and reclaimed {Bytes} bytes; skipped {Skipped}.",
                    deleted, reclaimedBytes, skipped);
            return new UpdateResult(deleted > 0);
        }
    }
}
