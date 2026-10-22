using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.AniList;
using Jularr.Backend.Data.User.AniList.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.AniList;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.AniList.V1
{
    // The profile's own AniList account, with the Owner's application behind it; the token is never shown.
    [ServiceEndpoint("GET", "/api/v1/anilist")]
    public sealed class ReadAniListConnectionV1 : UserService<AniListParametersV1>
    {
        private readonly AniListAccounts _accounts;

        internal ReadAniListConnectionV1(AniListAccounts accounts)
        {
            _accounts = accounts;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<AniListConnectionV1>()];
        protected override ModuleRequirement GetInstanceModules(AniListParametersV1 parameters) => ModuleRequirement.All("Library", "ProgressSync");
        protected override ServicePermission GetPermission(AniListParametersV1 parameters, Type resultType) => new("profile.self.update");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    coalesce(provider."IsEnabled" AND provider."ExternalId" IS NOT NULL AND provider."ProtectedSecret" IS NOT NULL, FALSE) AS "IsConfigured",
                    provider."ExternalId" AS "ClientId",
                    connection."ProfileId" IS NOT NULL AS "IsConnected",
                    connection."ViewerName",
                    connection."PushEnabledAt" IS NOT NULL AS "IsPushEnabled",
                    coalesce(connection."ExpiresAt" <= now(), FALSE) AS "IsExpired",
                    connection."ConnectedAt",
                    connection."SyncedAt",
                    connection."FailureCode",
                    (SELECT count(*) FROM "AniListEntries" AS entry WHERE entry."ProfileId" = @ProfileId)::integer AS "EntryCount",
                    (SELECT count(DISTINCT covered."MediaId") FROM "AniListEntryUnits" AS covered WHERE covered."ProfileId" = @ProfileId)::integer AS "MatchedCount"
                FROM
                    "Providers" AS provider
                    LEFT JOIN "AniListConnections" AS connection ON connection."ProfileId" = @ProfileId
                WHERE
                    provider."Key" = 'anilist';
                """;
            sql.Parameters.Add(new AniListProfileData(Auth.ProfileId!.Value));
            var row = await Context.ReadSql.ReadRequiredAsync<AniListConnectionRowData>(sql, CancellationToken);
            return new AniListConnectionV1(row.IsConfigured, row.IsConnected, row.ViewerName, row.IsPushEnabled, row.IsExpired, row.ConnectedAt, row.SyncedAt, row.FailureCode, row.EntryCount,
                row.MatchedCount, row.IsConfigured ? _accounts.AuthorizationUrl(row.ClientId) : null);
        }
    }

    // The code AniList's PIN page showed becomes the profile's connection; the first pass reads its list.
    [ServiceEndpoint("POST", "/api/v1/anilist")]
    public sealed class ConnectAniListV1 : UserService<AniListParametersV1, ConnectAniListDataV1>
    {
        private readonly AniListAccounts _accounts;

        internal ConnectAniListV1(AniListAccounts accounts)
        {
            _accounts = accounts;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AniListParametersV1 parameters) => ModuleRequirement.All("Library", "ProgressSync");
        protected override ServicePermission GetPermission(AniListParametersV1 parameters, Type resultType) => new("profile.self.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var connection = await _accounts.ConnectAsync(Context, Auth.ProfileId!.Value, Data.Code, CancellationToken);
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.SaveAniListConnectionAsync(connection, ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }
    }

    // Progress in Jularr stays; the connection and the list read from AniList go.
    [ServiceEndpoint("DELETE", "/api/v1/anilist")]
    public sealed class DisconnectAniListV1 : UserService<AniListParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AniListParametersV1 parameters) => ModuleRequirement.All("Library", "ProgressSync");
        protected override ServicePermission GetPermission(AniListParametersV1 parameters, Type resultType) => new("profile.self.update");

        protected override async Task<IServiceOutput> OnDelete()
        {
            await Context.DeleteAniListConnectionAsync(new AniListProfileData(Auth.ProfileId!.Value), CancellationToken);
            return new UpdateResult(true);
        }
    }

    // Whether progress made from now on is sent to AniList.
    [ServiceEndpoint("PUT", "/api/v1/anilist/push")]
    public sealed class UpdateAniListPushV1 : UserService<AniListParametersV1, UpdateAniListPushDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(AniListParametersV1 parameters) => ModuleRequirement.All("Library", "ProgressSync");
        protected override ServicePermission GetPermission(AniListParametersV1 parameters, Type resultType) => new("profile.self.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.SaveAniListPushAsync(new AniListPushSaveData(Auth.ProfileId!.Value, Data.IsPushEnabled), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
