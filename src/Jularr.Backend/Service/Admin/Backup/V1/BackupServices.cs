using System.Security.Cryptography;
using Jularr.Backend.Data.Admin.Backup.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Operation;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Operation;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Database;
using Jularr.Integrations.Logic.Database;
using Microsoft.Extensions.Options;
using OperationKind = Jularr.Backend.Data.Enums.Operation.OperationType;

namespace Jularr.Backend.Service.Admin.Backup.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/backups")]
    public sealed class ReadBackupsV1 : AdminService<BackupsParametersV1>
    {
        private readonly BackupOptions _options;
        private readonly PostgresBackupTools _tools;

        internal ReadBackupsV1(IOptions<BackupOptions> options, PostgresBackupTools tools)
        {
            _options = options.Value;
            _tools = tools;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BackupsV1>()];
        protected override ModuleRequirement GetInstanceModules(BackupsParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(BackupsParametersV1 parameters, Type resultType) => new("backup.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The latest backup operation and whether setup still accepts a restore.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    coalesce(latest."OperationStatusTypeId" IN (1, 2), FALSE) AS "IsRunning",
                    latest."ErrorKey",
                    setup."CompletedAt" IS NULL AS "CanRestore"
                FROM
                    "InstanceSetupState" AS setup
                    LEFT JOIN LATERAL (
                        SELECT
                            operation."OperationStatusTypeId",
                            operation."ErrorKey"
                        FROM
                            "Operations" AS operation
                        WHERE
                            operation."OperationTypeId" = 3
                        ORDER BY
                            operation."CreatedAt" DESC,
                            operation."Id" DESC
                        LIMIT 1
                    ) AS latest ON TRUE
                WHERE
                    setup."Id" = 1;
                """;
            var state = await Context.ReadSql.ReadRequiredAsync<BackupStateData>(sql, CancellationToken);
            var items = BackupFiles.List(_options.Path);
            return new BackupsV1(new BaseDataList<BackupV1>(items.Select(item => new BackupV1(item.Name, item.CreatedAt, item.SizeBytes, item.IsCompatible, item.IsSealed))), _tools.CanBackUp,
                state.IsRunning, state.ErrorKey, state.CanRestore && _tools.CanRestore);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/backups")]
    public sealed class QueueBackupV1 : AdminService<BackupsParametersV1, NoData>
    {
        private readonly BackupOptions _options;
        private readonly PostgresBackupTools _tools;
        private readonly WorkSignals _signals;

        internal QueueBackupV1(IOptions<BackupOptions> options, PostgresBackupTools tools, WorkSignals signals)
        {
            _options = options.Value;
            _tools = tools;
            _signals = signals;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(BackupsParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(BackupsParametersV1 parameters, Type resultType) => new("backup.update");

        protected override async Task<IServiceOutput> OnCreate()
        {
            if (!_tools.CanBackUp)
            {
                throw new ServiceUnavailableException("backup_unavailable");
            }

            return new UpdateResult(await Context.QueueBackupAsync(new OperationCreationData(OperationKind.Backup, null, Auth.AccountId), CancellationToken));
        }

        protected override void OnCommitted() => _signals.Backups.Notify();
    }

    [ServiceEndpoint("POST", "/api/v1/admin/backups/{name}/removal")]
    public sealed class DeleteBackupV1 : AdminService<BackupParametersV1, NoData>
    {
        private readonly BackupOptions _options;

        internal DeleteBackupV1(IOptions<BackupOptions> options)
        {
            _options = options.Value;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(BackupParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(BackupParametersV1 parameters, Type resultType) => new("backup.update");

        protected override Task<IServiceOutput> OnExecute()
        {
            Directory.Delete(BackupFiles.Resolve(_options.Path, Parameters.Name), true);
            return Task.FromResult<IServiceOutput>(new UpdateResult(true));
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/backups/{name}/restore")]
    public sealed class RestoreBackupV1 : AdminService<BackupParametersV1, RestoreBackupDataV1>
    {
        private static readonly SemaphoreSlim Running = new(1, 1);
        private readonly BackupOptions _options;
        private readonly PostgresBackupTools _tools;

        internal RestoreBackupV1(IOptions<BackupOptions> options, PostgresBackupTools tools)
        {
            _options = options.Value;
            _tools = tools;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(BackupParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(BackupParametersV1 parameters, Type resultType) => new("backup.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // A backup replaces only an instance that has not finished setup.
            var open = await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        setup."CompletedAt" IS NULL AS "CanRestore"
                    FROM
                        "InstanceSetupState" AS setup
                    WHERE
                        setup."Id" = 1;
                    """;
                return ctx.ReadSql.ReadRequiredAsync<BackupRestoreData>(sql, ct);
            }, CancellationToken);
            if (!open.CanRestore)
            {
                throw new DataValidationException(nameof(BackupParametersV1.Name), "setup_completed");
            }

            var folder = BackupFiles.Resolve(_options.Path, Parameters.Name);
            if (!BackupFiles.List(_options.Path).Any(item => item.Name == Parameters.Name && item.IsCompatible))
            {
                throw new DataValidationException(nameof(BackupParametersV1.Name), "backup_incompatible");
            }

            // One restore at a time; the key ring first, so a failure leaves the database untouched.
            if (!await Running.WaitAsync(0, CancellationToken))
            {
                throw new ServiceUnavailableException("backup_running");
            }

            try
            {
                var sealedKeys = Path.Combine(folder, BackupFiles.SealedKeysName);
                if (File.Exists(sealedKeys))
                {
                    try
                    {
                        BackupFiles.UnsealKeys(sealedKeys, _options.KeyDirectory, Data.Passphrase ?? "");
                    }
                    catch (CryptographicException)
                    {
                        throw new DataValidationException(nameof(Data.Passphrase), "backup_passphrase");
                    }
                }

                BackupFiles.CopyKeys(Path.Combine(folder, "keys"), _options.KeyDirectory);
                try
                {
                    await _tools.RestoreAsync(Path.Combine(folder, BackupFiles.DumpName), CancellationToken);
                }
                catch (PostgresToolException exception)
                {
                    throw new ServiceUnavailableException(exception.Code, exception);
                }
            }
            finally
            {
                Running.Release();
            }

            return new UpdateResult(true);
        }
    }
}
