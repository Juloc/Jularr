using Jularr.Backend.Data.Admin.System.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.User.Statistics.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Database;
using Jularr.Integrations.Logic.Media;
using Jularr.Integrations.Logic.Nas;
using Jularr.Integrations.Logic.Usenet;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Admin.System.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/system")]
    public sealed class ReadSystemHealthV1 : AdminService<ReadSystemHealthParametersV1>
    {
        private readonly MediaProbeClient _probe;
        private readonly MediaTranscoder _transcoder;
        private readonly ArchiveTools _archives;
        private readonly NasMountClient _mounts;
        private readonly PostgresBackupTools _backups;
        private readonly BackupOptions _backupOptions;

        internal ReadSystemHealthV1(MediaProbeClient probe, MediaTranscoder transcoder, ArchiveTools archives, NasMountClient mounts, PostgresBackupTools backups, IOptions<BackupOptions> backupOptions)
        {
            _probe = probe;
            _transcoder = transcoder;
            _archives = archives;
            _mounts = mounts;
            _backups = backups;
            _backupOptions = backupOptions.Value;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<SystemHealthV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadSystemHealthParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadSystemHealthParametersV1 parameters, Type resultType) => new("instance.settings.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Stored state only; nothing here touches storage or providers.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    pg_database_size(current_database()) AS "DatabaseBytes",
                    (SELECT count(*) FROM "Operations" AS operation WHERE operation."OperationStatusTypeId" IN (1, 2)) AS "QueuedOperations",
                    (SELECT count(*) FROM "Operations" AS operation WHERE operation."OperationStatusTypeId" = 4 AND operation."DismissedAt" IS NULL) AS "FailedOperations",
                    (SELECT count(*) FROM "Profiles") AS "Profiles",
                    (SELECT count(*) FROM "StorageMountHealth" AS health WHERE health."StorageHealthTypeId" IN (3, 4)) AS "StorageOffline";
                """;
            var totals = await Context.ReadSql.ReadRequiredAsync<SystemTotalsData>(sql, CancellationToken);

            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."MediaTypeId",
                    count(DISTINCT work."Id") AS "Count",
                    coalesce(sum(file."SizeBytes"), 0)::bigint AS "Bytes"
                FROM
                    "Works" AS work
                    JOIN "MediaAssets" AS asset ON asset."WorkId" = work."Id"
                    JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id" AND file."MissingSince" IS NULL
                GROUP BY
                    work."MediaTypeId"
                ORDER BY
                    work."MediaTypeId";
                """;
            var library = await Context.ReadSql.ReadListAsync<MediaTypeCountV1>(sql, CancellationToken);

            // Configured tools, not run.
            ToolStateV1[] tools =
            [
                new("ffprobe", _probe.IsAvailable),
                new("ffmpeg", _transcoder.IsAvailable),
                new("par2", _archives.CanRepair),
                new("sevenZip", _archives.CanExtract),
                new("mountHelper", _mounts.IsConfigured),
                new("pgDump", _backups.CanBackUp)
            ];
            var lastBackup = _backupOptions.Path is { } path ? BackupFiles.List(path).FirstOrDefault()?.CreatedAt : null;
            return new SystemHealthV1(totals.DatabaseBytes, totals.QueuedOperations, totals.FailedOperations, totals.Profiles, totals.StorageOffline, lastBackup,
                new BaseDataList<ToolStateV1>(tools), new BaseDataList<MediaTypeCountV1>(library));
        }
    }
}
