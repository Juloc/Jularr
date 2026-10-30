using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.AniList;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.User.AniList.V1;
using Jularr.Backend.Data.User.Media.V1;
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
        protected override ModuleRequirement GetInstanceModules(AniListParametersV1 parameters) => ModuleRequirement.All("Library", "ProgressSync", "AniList");
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
        protected override ModuleRequirement GetInstanceModules(AniListParametersV1 parameters) => ModuleRequirement.All("Library", "ProgressSync", "AniList");
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
        protected override ModuleRequirement GetInstanceModules(AniListParametersV1 parameters) => ModuleRequirement.All("Library", "ProgressSync", "AniList");
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
        protected override ModuleRequirement GetInstanceModules(AniListParametersV1 parameters) => ModuleRequirement.All("Library", "ProgressSync", "AniList");
        protected override ServicePermission GetPermission(AniListParametersV1 parameters, Type resultType) => new("profile.self.update");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            await Context.SaveAniListPushAsync(new AniListPushSaveData(Auth.ProfileId!.Value, Data.IsPushEnabled), CancellationToken);
            return new UpdateResult(true);
        }
    }

    // Where the profile stands with a title on its AniList list; the entry that is being followed stands for a title of several seasons.
    [ServiceEndpoint("GET", "/api/v1/works/{workId:long}/anilist")]
    public sealed class ReadWorkAniListV1 : UserService<WorkParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<WorkAniListV1>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library", "ProgressSync", "AniList");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // The entry: one being followed first, then the one changed last.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    entry."MediaId",
                    entry."IsManga",
                    entry."Status",
                    entry."Progress",
                    entry."ProgressVolumes",
                    entry."TotalCount",
                    entry."TotalVolumes"
                FROM
                    "AniListEntries" AS entry
                WHERE
                    entry."ProfileId" = @ProfileId
                    AND entry."MediaId" IN (
                        SELECT
                            covered."MediaId"
                        FROM
                            "AniListEntryUnits" AS covered
                        WHERE
                            covered."ProfileId" = entry."ProfileId"
                            AND covered."WorkId" = @WorkId
                    )
                ORDER BY
                    entry."Status" IN ('CURRENT', 'REPEATING') DESC,
                    entry."RemoteUpdatedAt" DESC
                LIMIT 1;
                """;
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            sql.Parameters.Add(Parameters);
            var entry = await Context.ReadSql.ReadOptionalAsync<WorkAniListEntryData>(sql, CancellationToken);
            if (entry is null)
            {
                // Not on the list, or no account at all.
                var connected = new SqlStatement();
                connected.Statement =
                    """
                    SELECT
                        EXISTS (SELECT 1 FROM "AniListConnections" AS connection WHERE connection."ProfileId" = @ProfileId) AS "IsConnected";
                    """;
                connected.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
                var flag = await Context.ReadSql.ReadRequiredAsync<AniListFlagData>(connected, CancellationToken);
                return new WorkAniListV1(flag.IsConnected, false, null, false, 0, null, 0, null);
            }

            // What the profile finished here, in a row from the first episode or volume.
            var units = new SqlStatement();
            units.Statement =
                """
                SELECT
                    covered."MediaId",
                    covered."WorkId",
                    covered."WorkUnitId",
                    covered."Number",
                    coalesce(progress."IsCompleted", FALSE) AS "IsCompleted",
                    progress."CompletedAt",
                    entry."IsManga" AS "IsVolume"
                FROM
                    "AniListEntryUnits" AS covered
                    JOIN "AniListEntries" AS entry ON entry."ProfileId" = covered."ProfileId" AND entry."MediaId" = covered."MediaId"
                    LEFT JOIN "WorkUnits" AS unit ON unit."Id" = covered."WorkUnitId"
                    LEFT JOIN "MediaProgress" AS progress ON progress."ProfileId" = covered."ProfileId"
                        AND progress."WorkId" = covered."WorkId"
                        AND progress."WorkUnitId" IS NOT DISTINCT FROM covered."WorkUnitId"
                WHERE
                    covered."ProfileId" = @ProfileId
                    AND covered."MediaId" = @MediaId
                    AND (
                        unit."Id" IS NULL
                        OR unit."ReleasedOn" IS NULL
                        OR unit."ReleasedOn" <= current_date
                        OR EXISTS (SELECT 1 FROM "MediaAssets" AS asset WHERE asset."WorkUnitId" = unit."Id")
                    )
                ORDER BY
                    covered."Number";
                """;
            units.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            units.Parameters.Add(new AniListMediaData(entry.MediaId));
            var local = AniListSyncRules.FinishedInARow(await Context.ReadSql.ReadListAsync<AniListUnitData>(units, CancellationToken));
            var remote = entry.IsManga ? entry.ProgressVolumes : entry.Progress;
            var total = entry.IsManga ? entry.TotalVolumes : entry.TotalCount;
            return new WorkAniListV1(true, true, entry.Status, entry.IsManga, remote, total, local, AniListSyncRules.StateOf(entry.Status, remote, total, local));
        }
    }

    // How many entries of the profile's list sit in each status.
    [ServiceEndpoint("GET", "/api/v1/anilist/lists")]
    public sealed class ReadAniListListsV1 : UserService<AniListParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<AniListStatusCountV1>>()];
        protected override ModuleRequirement GetInstanceModules(AniListParametersV1 parameters) => ModuleRequirement.All("Library", "ProgressSync", "AniList");
        protected override ServicePermission GetPermission(AniListParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    entry."Status",
                    count(*)::integer AS "Count"
                FROM
                    "AniListEntries" AS entry
                WHERE
                    entry."ProfileId" = @ProfileId
                GROUP BY
                    entry."Status";
                """;
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            return new BaseDataList<AniListStatusCountV1>(await Context.ReadSql.ReadListAsync<AniListStatusCountV1>(sql, CancellationToken));
        }
    }

    // One page of the profile's list in one status, the entries of a library title with its Work.
    [ServiceEndpoint("GET", "/api/v1/anilist/lists/{status}")]
    public sealed class ReadAniListEntriesV1 : UserService<AniListEntriesParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<PageResult<AniListEntryV1>>()];
        protected override ModuleRequirement GetInstanceModules(AniListEntriesParametersV1 parameters) => ModuleRequirement.All("Library", "ProgressSync", "AniList");
        protected override ServicePermission GetPermission(AniListEntriesParametersV1 parameters, Type resultType) => new("profile.media.read");
        protected override PageRequest GetPageRequest(AniListEntriesParametersV1 parameters) => new(parameters.Page, parameters.PageSize);

        protected override async Task<IServiceOutput> OnRead()
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    entry."MediaId",
                    (
                        SELECT
                            min(covered."WorkId")
                        FROM
                            "AniListEntryUnits" AS covered
                            JOIN "ProfileWorks" AS visible ON visible."ProfileId" = covered."ProfileId" AND visible."WorkId" = covered."WorkId"
                        WHERE
                            covered."ProfileId" = entry."ProfileId"
                            AND covered."MediaId" = entry."MediaId"
                    ) AS "WorkId",
                    entry."Title",
                    entry."CoverUrl",
                    entry."IsManga",
                    entry."Status",
                    entry."Progress",
                    entry."TotalCount" AS "Total"
                FROM
                    "AniListEntries" AS entry
                WHERE
                    entry."ProfileId" = @ProfileId
                    AND entry."Status" = @Status
                ORDER BY
                    entry."RemoteUpdatedAt" DESC,
                    entry."MediaId"
                LIMIT @PageSize
                OFFSET @Offset;
                """;
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            sql.Parameters.Add(new AniListStatusData(Parameters.Status));
            sql.Page = Page;
            return await Context.ReadSql.ReadPageAsync<AniListEntryV1>(sql, CancellationToken);
        }
    }
}
