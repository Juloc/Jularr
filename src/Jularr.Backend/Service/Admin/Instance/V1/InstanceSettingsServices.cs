using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Data.Logic.Worker;
using Jularr.Backend.Logic.Worker;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Instance;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Instance.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/instance/settings")]
    public sealed class ReadInstanceSettingsV1 : AdminService<ReadInstanceSettingsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<InstanceSettingsDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadInstanceSettingsParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(ReadInstanceSettingsParametersV1 parameters, Type resultType) => new("instance.settings.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Read instance configuration.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    settings."Name",
                    settings."UiLocaleId",
                    settings."Version",
                    (settings."ReadingCacheBytes" / 1048576)::integer AS "ReadingCacheMegabytes",
                    settings."ReadingCacheWorks",
                    settings."UsersConfirmSources",
                    settings."LibraryScanIntervalHours",
                    settings."HistoryRetentionDays",
                    settings."HistoryMegabytes",
                    settings."CollectionLimit",
                    settings."UnitGroupLimit",
                    settings."SubtitleLanguages"::text[] AS "SubtitleLanguages",
                    settings."PublicUrl",
                    settings."TrustedProxies"::text[] AS "TrustedProxies",
                    settings."BackupIntervalHours",
                    settings."BackupKeep",
                    settings."ProviderSyncIntervalMinutes",
                    settings."WantedSearchIntervalMinutes"
                FROM
                    "InstanceSettings" AS settings
                WHERE
                    settings."Id" = 1;
                """;
            return await Context.ReadSql.ReadRequiredAsync<InstanceSettingsDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/instance/settings")]
    public sealed class UpdateInstanceSettingsV1 : AdminService<UpdateInstanceSettingsParametersV1, UpdateInstanceSettingsDataV1>
    {
        private readonly TrustedProxies _proxies;
        private readonly WorkerJobs _jobs;
        private string[]? _saved;
        private WorkerSettingsData? _workerSettings;

        internal UpdateInstanceSettingsV1(TrustedProxies proxies, WorkerJobs jobs)
        {
            _proxies = proxies;
            _jobs = jobs;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(UpdateInstanceSettingsParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(UpdateInstanceSettingsParametersV1 parameters, Type resultType) => new("instance.settings.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            // Use the canonical writer; the stored proxies apply once committed.
            await Context.UpdateInstanceSettingsAsync(new InstanceSettingsUpdateData(Data.Name, Data.UiLocaleId, Data.Version, Data.ReadingCacheMegabytes, Data.ReadingCacheWorks,
                Data.UsersConfirmSources, Data.LibraryScanIntervalHours, Data.HistoryRetentionDays, Data.HistoryMegabytes,
                Data.CollectionLimit, Data.UnitGroupLimit, Data.SubtitleLanguages, Data.PublicUrl, Data.TrustedProxies, Data.BackupIntervalHours, Data.BackupKeep, Data.ProviderSyncIntervalMinutes,
                Data.WantedSearchIntervalMinutes), CancellationToken);
            _saved = await Context.ReadTrustedProxiesAsync(CancellationToken);
            _workerSettings = await Context.ReadWorkerSettingsAsync(CancellationToken);
            return new UpdateResult(true);
        }

        protected override void OnCommitted()
        {
            if (_saved is { } proxies) _proxies.Use(proxies);

            // The workers follow the new intervals from their next look.
            if (_workerSettings is { } settings) _jobs.UseSettings(settings);
        }
    }
}
