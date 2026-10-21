using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Operation;
using Jularr.Backend.Data.System.Backup.V1;
using Jularr.Backend.Data.System.Operation.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Operation;
using Jularr.Backend.Logic.Worker;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Database;
using Jularr.Integrations.Logic.Database;
using Microsoft.Extensions.Options;
using OperationKind = Jularr.Backend.Data.Enums.Operation.OperationType;

namespace Jularr.Backend.Service.System.Backup.V1
{
    public sealed class ExecuteBackupV1 : SystemService<OperationParametersV1, NoData>
    {
        private readonly BackupOptions _options;
        private readonly PostgresBackupTools _tools;
        private readonly TimeProvider _time;

        internal ExecuteBackupV1(IOptions<BackupOptions> options, PostgresBackupTools tools, TimeProvider time)
        {
            _options = options.Value;
            _tools = tools;
            _time = time;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(OperationParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(OperationParametersV1 parameters, Type resultType) => new("system.operations");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var root = _options.Path;
            var settings = await Context.ExecuteBatchAsync((ctx, ct) => ctx.ReadWorkerSettingsAsync(ct), CancellationToken);
            if (!_tools.CanBackUp)
            {
                throw new ServiceUnavailableException("backup_unavailable");
            }

            try
            {
                await BackupFiles.CreateAsync(_options, _tools, _time.GetUtcNow().UtcDateTime, DatabaseBaseline.CurrentVersion, BackupFiles.SchemaFingerprint, CancellationToken);
            }
            catch (PostgresToolException exception)
            {
                throw new ServiceUnavailableException(exception.Code, exception);
            }

            // The backup counts even when pruning fails.
            await Context.ExecuteBatchAsync((ctx, ct) => ctx.UpdateOperationAsync(new OperationProgressData(Parameters.OperationId, 1, OperationStatusType.Completed, null), ct), CancellationToken);
            BackupFiles.Prune(root, settings.BackupKeep);
            return new UpdateResult(true);
        }
    }

    // Queues a backup when neither a backup nor an attempt is newer than the interval, or at once when the Owner ran the job.
    public sealed class ScheduleBackupV1 : SystemService<BackupScheduleParametersV1, NoData>
    {
        private readonly BackupOptions _options;
        private readonly PostgresBackupTools _tools;
        private readonly TimeProvider _time;
        private readonly WorkSignals _signals;
        private bool _isQueued;

        internal ScheduleBackupV1(IOptions<BackupOptions> options, PostgresBackupTools tools, TimeProvider time, WorkSignals signals)
        {
            _options = options.Value;
            _tools = tools;
            _time = time;
            _signals = signals;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(BackupScheduleParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(BackupScheduleParametersV1 parameters, Type resultType) => new("system.operations");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            var root = _options.Path;
            if (Parameters.IsForced)
            {
                // The Owner asked for one: it is queued unless one is already waiting or running.
                _isQueued = _tools.CanBackUp && await Context.QueueBackupAsync(new OperationCreationData(OperationKind.Backup, null, null), CancellationToken);
                return new UpdateResult(_isQueued);
            }

            var intervalHours = (await Context.ReadWorkerSettingsAsync(CancellationToken)).BackupIntervalHours;
            var since = _time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(Math.Max(1, intervalHours));
            if (!_tools.CanBackUp || intervalHours <= 0 || BackupFiles.Newest(root) > since)
            {
                return new UpdateResult(false);
            }

            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    EXISTS (
                        SELECT
                            1
                        FROM
                            "Operations" AS operation
                        WHERE
                            operation."OperationTypeId" = 3
                            AND operation."CreatedAt" > @Since
                    ) AS "HasRecentAttempt";
                """;
            sql.Parameters.Add(new OperationSinceData(since));
            if ((await Context.ReadSql.ReadRequiredAsync<BackupAttemptData>(sql, CancellationToken)).HasRecentAttempt)
            {
                return new UpdateResult(false);
            }

            _isQueued = await Context.QueueBackupAsync(new OperationCreationData(OperationKind.Backup, null, null), CancellationToken);
            return new UpdateResult(_isQueued);
        }

        // The backup lane looks once the backup is committed.
        protected override void OnCommitted()
        {
            if (_isQueued) _signals.Backups.Notify();
        }
    }
}
