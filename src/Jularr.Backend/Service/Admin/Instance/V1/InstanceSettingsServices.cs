using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Instance;
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
                    settings."MetadataTrendingHours",
                    settings."MetadataRatingsHours",
                    settings."MetadataFreshDays",
                    settings."DiscoveryCacheEntries",
                    settings."DiscoveryCacheMegabytes",
                    settings."UsersConfirmSources",
                    settings."LibraryScanIntervalHours",
                    settings."HistoryRetentionDays",
                    settings."HistoryMegabytes",
                    settings."CollectionLimit",
                    settings."UnitGroupLimit",
                    settings."SubtitleLanguages"::text[] AS "SubtitleLanguages",
                    settings."PublicUrl",
                    settings."TrustedProxies"::text[] AS "TrustedProxies"
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
        private string[]? _saved;

        internal UpdateInstanceSettingsV1(TrustedProxies proxies)
        {
            _proxies = proxies;
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
                Data.CollectionLimit, Data.UnitGroupLimit, Data.SubtitleLanguages, Data.PublicUrl, Data.TrustedProxies,
                Data.MetadataTrendingHours, Data.MetadataRatingsHours, Data.MetadataFreshDays, Data.DiscoveryCacheEntries, Data.DiscoveryCacheMegabytes), CancellationToken);
            _saved = await Context.ReadTrustedProxiesAsync(CancellationToken);
            return new UpdateResult(true);
        }

        protected override void OnCommitted()
        {
            if (_saved is { } proxies) _proxies.Use(proxies);
        }
    }
}
