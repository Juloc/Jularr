using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Operation;
using Jularr.Backend.Data.System.Backup.V1;
using Jularr.Backend.Data.System.Operation.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Operation;
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
            if (_options.Path is not { } root || !_tools.CanBackUp)
            {
                throw new ServiceUnavailableException("backup_unavailable");
            }

            // A half-written backup never shows up in the list.
            var now = _time.GetUtcNow().UtcDateTime;
            var name = BackupFiles.NewName(now);
            var partial = Path.Combine(root, name + ".partial");
            BackupFiles.CreatePrivate(partial);
            try
            {
                try
                {
                    await _tools.DumpAsync(Path.Combine(partial, BackupFiles.DumpName), CancellationToken);
                }
                catch (PostgresToolException exception)
                {
                    throw new ServiceUnavailableException(exception.Code, exception);
                }

                BackupFiles.CopyKeys(_options.KeyDirectory, Path.Combine(partial, "keys"));
                await BackupFiles.WriteManifestAsync(partial, now, CancellationToken);
                Directory.Move(partial, Path.Combine(root, name));
            }
            finally
            {
                if (Directory.Exists(partial)) Directory.Delete(partial, true);
            }

            // The backup counts even when pruning fails.
            await Context.ExecuteBatchAsync((ctx, ct) => ctx.UpdateOperationAsync(new OperationProgressData(Parameters.OperationId, 1, OperationStatusType.Completed, null), ct), CancellationToken);
            BackupFiles.Prune(root, _options.Keep);
            return new UpdateResult(true);
        }
    }

    // Queues a backup when neither a backup nor an attempt is newer than the interval.
    public sealed class ScheduleBackupV1 : SystemService<BackupScheduleParametersV1, NoData>
    {
        private readonly BackupOptions _options;
        private readonly PostgresBackupTools _tools;
        private readonly TimeProvider _time;

        internal ScheduleBackupV1(IOptions<BackupOptions> options, PostgresBackupTools tools, TimeProvider time)
        {
            _options = options.Value;
            _tools = tools;
            _time = time;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(BackupScheduleParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(BackupScheduleParametersV1 parameters, Type resultType) => new("system.operations");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            var since = _time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(Math.Max(1, _options.IntervalHours));
            if (_options.Path is not { } root || !_tools.CanBackUp || _options.IntervalHours <= 0 || BackupFiles.Newest(root) > since)
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

            return new UpdateResult(await Context.QueueBackupAsync(new OperationCreationData(OperationKind.Backup, null, null), CancellationToken));
        }
    }
}
