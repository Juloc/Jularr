using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Provider;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Provider;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Provider;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.MediaServer;

namespace Jularr.Backend.Service.Admin.Provider.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/media-servers/{mediaServerTypeId}")]
    public sealed class ReadMediaServerV1 : AdminService<MediaServerParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<MediaServerDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(MediaServerParametersV1 parameters) => ModuleRequirement.All("ProgressSync");
        protected override ServicePermission GetPermission(MediaServerParametersV1 parameters, Type resultType) => new("providers.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The API key stays write-only.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    provider."IsEnabled",
                    provider."BaseUrl",
                    provider."ProtectedSecret" IS NOT NULL AS "HasSecret",
                    provider."ExternalId",
                    provider."SyncedAt"
                FROM
                    "Providers" AS provider
                WHERE
                    provider."Key" = @ProviderKey;
                """;
            sql.Parameters.Add(new ProviderParameters(MediaServers.ProviderKey(Parameters.MediaServerTypeId)));
            return await Context.ReadSql.ReadRequiredAsync<MediaServerDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/media-servers/{mediaServerTypeId}")]
    public sealed class SaveMediaServerV1 : AdminService<MediaServerParametersV1, SaveMediaServerDataV1>
    {
        private readonly MediaServers _servers;

        internal SaveMediaServerV1(MediaServers servers)
        {
            _servers = servers;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<MediaServerTestV1>()];
        protected override ModuleRequirement GetInstanceModules(MediaServerParametersV1 parameters) => ModuleRequirement.All("ProgressSync");
        protected override ServicePermission GetPermission(MediaServerParametersV1 parameters, Type resultType) => new("providers.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Only a server that answers with this key is stored, also below a path of a reverse proxy; its users are proposed right away.
            var baseUrl = new Uri(Data.BaseUrl.TrimEnd('/') + "/");
            if (baseUrl.Query.Length > 0 || baseUrl.Fragment.Length > 0)
            {
                throw new DataValidationException(nameof(SaveMediaServerDataV1.BaseUrl), "invalid_url");
            }

            var type = Parameters.MediaServerTypeId;
            var apiKey = Data.ApiKey ?? (await _servers.ConnectAsync(Context, type, CancellationToken))?.ApiKey
                ?? throw new DataValidationException(nameof(SaveMediaServerDataV1.ApiKey), "required");
            var connection = new MediaServerConnection(baseUrl, apiKey, MediaServers.Dialect(type));
            var server = await _servers.ReadServerAsync(connection, CancellationToken);
            if (type == MediaServerType.Jellyfin && (!Version.TryParse(server.Version, out var version) || version < new Version(10, 9)))
            {
                throw new ServiceUnavailableException("media_server_outdated");
            }

            var data = new MediaServerSaveData(MediaServers.ProviderKey(type), baseUrl.ToString(), Data.ApiKey is null ? null : _servers.Protect(apiKey), server.Id);
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.SaveMediaServerAsync(data, ct);
                return true;
            }, CancellationToken);
            var users = await _servers.RefreshUsersAsync(Context, type, connection, CancellationToken);
            return new MediaServerTestV1(server.Name, server.Version, users);
        }
    }

    [ServiceEndpoint("DELETE", "/api/v1/admin/media-servers/{mediaServerTypeId}")]
    public sealed class DisconnectMediaServerV1 : AdminService<MediaServerParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(MediaServerParametersV1 parameters) => ModuleRequirement.All("ProgressSync");
        protected override ServicePermission GetPermission(MediaServerParametersV1 parameters, Type resultType) => new("providers.update");

        protected override async Task<IServiceOutput> OnDelete()
        {
            // Progress in Jularr stays; links and the agreed state go.
            await Context.SaveMediaServerAsync(new MediaServerSaveData(MediaServers.ProviderKey(Parameters.MediaServerTypeId), null, null, null), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/media-servers/{mediaServerTypeId}/links")]
    public sealed class ReadMediaServerLinksV1 : AdminService<MediaServerParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<MediaServerLinkV1>>()];
        protected override ModuleRequirement GetInstanceModules(MediaServerParametersV1 parameters) => ModuleRequirement.All("ProgressSync");
        protected override ServicePermission GetPermission(MediaServerParametersV1 parameters, Type resultType) => new("providers.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    link."Id",
                    link."ExternalName",
                    link."ProfileId",
                    profile."DisplayName" AS "ProfileName",
                    link."IsConfirmed",
                    link."SyncedAt"
                FROM
                    "ProviderProfileLinks" AS link
                    JOIN "Providers" AS provider ON provider."Id" = link."ProviderId"
                    LEFT JOIN "Profiles" AS profile ON profile."Id" = link."ProfileId"
                WHERE
                    provider."Key" = @ProviderKey
                ORDER BY
                    link."IsConfirmed",
                    lower(link."ExternalName"),
                    link."Id";
                """;
            sql.Parameters.Add(new ProviderParameters(MediaServers.ProviderKey(Parameters.MediaServerTypeId)));
            return new BaseDataList<MediaServerLinkV1>(await Context.ReadSql.ReadListAsync<MediaServerLinkV1>(sql, CancellationToken));
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/media-servers/{mediaServerTypeId}/links/{linkId:long}")]
    public sealed class SaveMediaServerLinkV1 : AdminService<MediaServerLinkParametersV1, SaveMediaServerLinkDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(MediaServerLinkParametersV1 parameters) => ModuleRequirement.All("ProgressSync");
        protected override ServicePermission GetPermission(MediaServerLinkParametersV1 parameters, Type resultType) => new("providers.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.SaveMediaServerLinkAsync(new MediaServerLinkSaveData(MediaServers.ProviderKey(Parameters.MediaServerTypeId), Parameters.LinkId, Data.ProfileId),
                CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/media-servers/{mediaServerTypeId}/sync")]
    public sealed class SyncMediaServerV1 : AdminService<MediaServerParametersV1, NoData>
    {
        private readonly MediaServers _servers;

        internal SyncMediaServerV1(MediaServers servers)
        {
            _servers = servers;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<MediaServerSyncResultV1>()];
        protected override ModuleRequirement GetInstanceModules(MediaServerParametersV1 parameters) => ModuleRequirement.All("ProgressSync");
        protected override ServicePermission GetPermission(MediaServerParametersV1 parameters, Type resultType) => new("providers.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var type = Parameters.MediaServerTypeId;
            var connection = await _servers.ConnectAsync(Context, type, CancellationToken) ?? throw new ServiceUnavailableException("media_server_unavailable");
            var (local, remote) = await _servers.SyncAsync(Context, type, connection, CancellationToken);
            return new MediaServerSyncResultV1(local, remote);
        }
    }
}
