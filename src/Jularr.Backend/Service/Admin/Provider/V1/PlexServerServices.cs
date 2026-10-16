using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Provider.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Provider;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Provider;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Plex;

namespace Jularr.Backend.Service.Admin.Provider.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/providers/plex")]
    public sealed class ReadPlexServerV1 : AdminService<PlexServerParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PlexServerDetailsV1>()];
        protected override ModuleRequirement GetInstanceModules(PlexServerParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(PlexServerParametersV1 parameters, Type resultType) => new("providers.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The token stays write-only.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    provider."IsEnabled",
                    provider."BaseUrl",
                    provider."ProtectedSecret" IS NOT NULL AS "HasSecret",
                    provider."ExternalId",
                    provider."SyncedAt",
                    (
                        SELECT
                            count(*)::integer
                        FROM
                            "WorkExternalIdentities" AS identity
                        WHERE
                            identity."Namespace" = 'plex'
                    ) AS "LinkedCount"
                FROM
                    "Providers" AS provider
                WHERE
                    provider."Key" = 'plex';
                """;
            return await Context.ReadSql.ReadRequiredAsync<PlexServerDetailsV1>(sql, CancellationToken);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/providers/plex")]
    public sealed class SavePlexServerV1 : AdminService<PlexServerParametersV1, SavePlexServerDataV1>
    {
        private readonly PlexServer _plex;

        internal SavePlexServerV1(PlexServer plex)
        {
            _plex = plex;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PlexServerTestV1>()];
        protected override ModuleRequirement GetInstanceModules(PlexServerParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(PlexServerParametersV1 parameters, Type resultType) => new("providers.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Only a server that answers with this token is stored.
            var baseUrl = ProviderLogic.ServerAddress(Data.BaseUrl, nameof(SavePlexServerDataV1.BaseUrl));

            var token = Data.Token;
            if (token is null)
            {
                token = (await _plex.ConnectAsync(Context, CancellationToken))?.Token ?? throw new DataValidationException(nameof(SavePlexServerDataV1.Token), "required");
            }

            var server = await _plex.ReadServerAsync(new PlexServerConnection(baseUrl, token), CancellationToken);
            var data = new PlexServerSaveData(baseUrl.AbsoluteUri, Data.Token is null ? null : _plex.Protect(token), server.MachineIdentifier);
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.SavePlexServerAsync(data, ct);
                return true;
            }, CancellationToken);
            return new PlexServerTestV1(server.MachineIdentifier, server.Version, [.. server.Sections.Select(section => section.Title)]);
        }
    }

    [ServiceEndpoint("DELETE", "/api/v1/admin/providers/plex")]
    public sealed class DisconnectPlexServerV1 : AdminService<PlexServerParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(PlexServerParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(PlexServerParametersV1 parameters, Type resultType) => new("providers.update");

        protected override async Task<IServiceOutput> OnDelete()
        {
            // Works stay; only the Plex links go.
            await Context.SavePlexServerAsync(new PlexServerSaveData(null, null, null), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/providers/plex/sync")]
    public sealed class SyncPlexServerV1 : AdminService<PlexServerParametersV1, NoData>
    {
        private readonly PlexServer _plex;

        internal SyncPlexServerV1(PlexServer plex)
        {
            _plex = plex;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PlexSyncResultV1>()];
        protected override ModuleRequirement GetInstanceModules(PlexServerParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(PlexServerParametersV1 parameters, Type resultType) => new("providers.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var connection = await _plex.ConnectAsync(Context, CancellationToken) ?? throw new ServiceUnavailableException("plex_unavailable");
            var result = await _plex.SyncAsync(Context, connection, CancellationToken);
            return new PlexSyncResultV1(result.LinkedCount, result.CreatedCount);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/admin/providers/plex/sign-in")]
    public sealed class ReadPlexSignInV1 : AdminService<PlexServerParametersV1>
    {
        private readonly PlexServer _plex;

        internal ReadPlexSignInV1(PlexServer plex)
        {
            _plex = plex;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PlexSignInV1>()];
        protected override ModuleRequirement GetInstanceModules(PlexServerParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(PlexServerParametersV1 parameters, Type resultType) => new("providers.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var settings = await Context.ReadPlexSignInAsync(CancellationToken);

            // The libraries come from the server while it answers; the chosen keys stay either way.
            IReadOnlyList<PlexSection> sections = [];
            if (_plex.Connect(await Context.ReadPlexServerAsync(CancellationToken)) is { } connection)
            {
                try
                {
                    sections = (await _plex.ReadServerAsync(connection, CancellationToken)).Sections;
                }
                catch (ServiceUnavailableException)
                {
                }
            }

            return new PlexSignInV1(settings.SignIn, settings.Registration, settings.RegistrationApproval, settings.RegistrationSections,
                [.. sections.Select(section => section.Key)], [.. sections.Select(section => section.Title)]);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/admin/providers/plex/sign-in")]
    public sealed class UpdatePlexSignInV1 : AdminService<PlexServerParametersV1, SavePlexSignInDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(PlexServerParametersV1 parameters) => ModuleRequirement.None;
        protected override ServicePermission GetPermission(PlexServerParametersV1 parameters, Type resultType) => new("providers.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.SavePlexSignInAsync(new PlexSignInSaveData(Data.SignIn, Data.Registration && Data.SignIn, Data.RegistrationApproval, Data.RegistrationSections), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
