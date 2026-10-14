using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Playback;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Provider;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Integrations.Data.MediaServer;

namespace Jularr.Backend.Logic.Provider
{
    // Jellyfin and Emby: the connection, which user shares progress with which profile, and how progress is merged.
    internal static class MediaServerLogic
    {
        public static Task<MediaServerStateData?> ReadMediaServerAsync(this LogicContext ctx, ProviderParameters parameters, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    provider."Id",
                    provider."IsEnabled",
                    provider."BaseUrl",
                    provider."ProtectedSecret",
                    provider."ExternalId"
                FROM
                    "Providers" AS provider
                WHERE
                    provider."Key" = @ProviderKey;
                """;
            sql.Parameters.Add(parameters);
            return ctx.ReadSql.ReadOptionalAsync<MediaServerStateData>(sql, ct);
        }

        public static async Task SaveMediaServerAsync(this LogicContext ctx, MediaServerSaveData data, CancellationToken ct)
        {
            // Another server has other users; its links and agreed progress start over.
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "ProviderProfileLinks" AS link
                USING
                    "Providers" AS provider
                WHERE
                    provider."Id" = link."ProviderId"
                    AND provider."Key" = @ProviderKey
                    AND provider."ExternalId" IS DISTINCT FROM @ExternalId;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            var update = new SqlStatement();
            update.Statement =
                """
                UPDATE
                    "Providers" AS provider
                SET
                    "BaseUrl" = @BaseUrl,
                    "ProtectedSecret" = CASE WHEN @BaseUrl::text IS NULL THEN NULL ELSE coalesce(@ProtectedSecret, provider."ProtectedSecret") END,
                    "SyncedAt" = CASE WHEN provider."ExternalId" IS NOT DISTINCT FROM @ExternalId THEN provider."SyncedAt" END,
                    "ExternalId" = @ExternalId
                WHERE
                    provider."Key" = @ProviderKey;
                """;
            update.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(update, ct) != 1)
            {
                throw new KeyNotFoundException("The media server provider is not registered.");
            }
        }

        public static async Task SaveMediaServerUsersAsync(this LogicContext ctx, MediaServerUsersData data, CancellationToken ct)
        {
            // Users gone from the server lose their link; new ones are proposed the one profile carrying their name.
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH remote AS (
                    SELECT
                        item."Id",
                        item."Name"
                    FROM
                        jsonb_to_recordset(@Users::jsonb) AS item("Id" text, "Name" text)
                ),
                provider AS (
                    SELECT
                        provider."Id"
                    FROM
                        "Providers" AS provider
                    WHERE
                        provider."Key" = @ProviderKey
                ),
                removed AS (
                    DELETE FROM
                        "ProviderProfileLinks" AS link
                    USING
                        provider
                    WHERE
                        link."ProviderId" = provider."Id"
                        AND NOT EXISTS (
                            SELECT
                                1
                            FROM
                                remote
                            WHERE
                                remote."Id" = link."ExternalUserId"
                        )
                )
                INSERT INTO
                    "ProviderProfileLinks" ("ProviderId", "ExternalUserId", "ExternalName", "ProfileId")
                SELECT
                    provider."Id",
                    remote."Id",
                    remote."Name",
                    proposal."ProfileId"
                FROM
                    remote
                    CROSS JOIN provider
                    LEFT JOIN LATERAL (
                        SELECT
                            min(profile."Id") AS "ProfileId"
                        FROM
                            "Profiles" AS profile
                            JOIN "Accounts" AS account ON account."Id" = profile."OwnerAccountId"
                        WHERE
                            (
                                lower(profile."DisplayName") = lower(remote."Name")
                                OR lower(account."DisplayName") = lower(remote."Name")
                                    AND profile."Id" = (
                                        SELECT
                                            min(own."Id")
                                        FROM
                                            "Profiles" AS own
                                        WHERE
                                            own."OwnerAccountId" = account."Id"
                                    )
                            )
                            AND NOT EXISTS (
                                SELECT
                                    1
                                FROM
                                    "ProviderProfileLinks" AS taken
                                WHERE
                                    taken."ProviderId" = provider."Id"
                                    AND taken."ProfileId" = profile."Id"
                                    AND taken."IsConfirmed"
                            )
                        HAVING
                            count(DISTINCT profile."Id") = 1
                    ) AS proposal ON TRUE
                ON CONFLICT ("ProviderId", "ExternalUserId") DO UPDATE
                SET
                    "ExternalName" = EXCLUDED."ExternalName",
                    "ProfileId" = CASE WHEN "ProviderProfileLinks"."IsConfirmed" THEN "ProviderProfileLinks"."ProfileId" ELSE EXCLUDED."ProfileId" END;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task SaveMediaServerLinkAsync(this LogicContext ctx, MediaServerLinkSaveData data, CancellationToken ct)
        {
            if (data.ProfileId is not null)
            {
                var profile = new SqlStatement();
                profile.Statement =
                    """
                    SELECT
                        profile."Id" AS "ProviderProfileLinkId"
                    FROM
                        "Profiles" AS profile
                    WHERE
                        profile."Id" = @ProfileId;
                    """;
                profile.Parameters.Add(data);
                _ = await ctx.ReadOptionalAsync<MediaServerLinkIdData>(profile, ct) ?? throw new KeyNotFoundException("The profile was not found.");
            }

            // A profile shares progress with one user of a server at a time.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "ProviderProfileLinks" AS link
                SET
                    "ProfileId" = @ProfileId,
                    "IsConfirmed" = TRUE
                FROM
                    "Providers" AS provider
                WHERE
                    provider."Id" = link."ProviderId"
                    AND provider."Key" = @ProviderKey
                    AND link."Id" = @LinkId
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "ProviderProfileLinks" AS taken
                        WHERE
                            taken."ProviderId" = link."ProviderId"
                            AND taken."ProfileId" = @ProfileId
                            AND taken."IsConfirmed"
                            AND taken."Id" <> link."Id"
                    );
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) == 1)
            {
                return;
            }

            var exists = new SqlStatement();
            exists.Statement =
                """
                SELECT
                    link."Id" AS "ProviderProfileLinkId"
                FROM
                    "ProviderProfileLinks" AS link
                    JOIN "Providers" AS provider ON provider."Id" = link."ProviderId"
                WHERE
                    provider."Key" = @ProviderKey
                    AND link."Id" = @LinkId;
                """;
            exists.Parameters.Add(data);
            _ = await ctx.ReadOptionalAsync<MediaServerLinkIdData>(exists, ct) ?? throw new KeyNotFoundException("The media server user was not found.");
            throw new DataValidationException(nameof(data.ProfileId), "duplicate_value");
        }

        public static Task<IReadOnlyList<MediaServerSyncLinkData>> ReadSyncLinksAsync(this LogicContext ctx, ProviderParameters parameters, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    link."Id",
                    link."ExternalUserId",
                    link."ProfileId"
                FROM
                    "ProviderProfileLinks" AS link
                    JOIN "Providers" AS provider ON provider."Id" = link."ProviderId"
                WHERE
                    provider."Key" = @ProviderKey
                    AND link."IsConfirmed"
                    AND link."ProfileId" IS NOT NULL
                ORDER BY
                    link."Id";
                """;
            sql.Parameters.Add(parameters);
            return ctx.ReadSql.ReadListAsync<MediaServerSyncLinkData>(sql, ct);
        }

        public static async Task<IReadOnlyList<ProgressCandidateData>> ReadProgressCandidatesAsync(this LogicContext ctx, MediaServerItemsData data, CancellationToken ct)
        {
            // Items the profile may see, matched by provider id and for episodes by season and number; untouched ones are left out.
            var matches = await ctx.MatchWorksByIdentityAsync(new WorkIdentityRowsData(data.Items), ct);
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    item."Key" AS "ExternalItemId",
                    item."PositionMs" AS "RemotePositionMs",
                    item."IsPlayed" AS "RemoteIsCompleted",
                    item."DurationMs",
                    work."WorkId",
                    unit."Id" AS "WorkUnitId",
                    progress."PositionMs" AS "LocalPositionMs",
                    progress."IsCompleted" AS "LocalIsCompleted",
                    progress."UpdatedAt" AS "LocalUpdatedAt",
                    state."PositionMs" AS "SyncedPositionMs",
                    state."IsCompleted" AS "SyncedIsCompleted",
                    state."SyncedAt",
                    now() AS "ReadAt"
                FROM
                    jsonb_to_recordset(@Items::jsonb) AS item("Key" text, "Season" integer, "Episode" integer, "PositionMs" bigint, "IsPlayed" boolean, "DurationMs" bigint)
                    JOIN jsonb_to_recordset(@Matches::jsonb) AS work ("Key" text, "WorkId" bigint) ON work."Key" = item."Key"
                    JOIN "ProfileWorks" AS visible ON visible."WorkId" = work."WorkId" AND visible."ProfileId" = @ProfileId
                    LEFT JOIN LATERAL (
                        SELECT
                            episode."Id"
                        FROM
                            "WorkUnits" AS season
                            JOIN "WorkUnits" AS episode ON episode."WorkId" = season."WorkId" AND episode."ParentWorkUnitId" = season."Id"
                        WHERE
                            season."WorkId" = work."WorkId"
                            AND season."WorkUnitTypeId" = 1
                            AND season."Number" = item."Season"
                            AND episode."WorkUnitTypeId" = 2
                            AND episode."Number" = item."Episode"
                        LIMIT 1
                    ) AS unit ON TRUE
                    LEFT JOIN "MediaProgress" AS progress ON progress."ProfileId" = @ProfileId
                        AND progress."WorkId" = work."WorkId"
                        AND progress."WorkUnitId" IS NOT DISTINCT FROM unit."Id"
                    LEFT JOIN "ProviderProgress" AS state ON state."ProviderProfileLinkId" = @ProviderProfileLinkId AND state."ExternalItemId" = item."Key"
                WHERE
                    (item."Episode" IS NULL OR unit."Id" IS NOT NULL)
                    AND (item."PositionMs" > 0 OR item."IsPlayed" OR progress."Id" IS NOT NULL OR state."ExternalItemId" IS NOT NULL);
                """;
            sql.Parameters.Add(data);
            sql.Parameters.Add(matches);
            return await ctx.ReadSql.ReadListAsync<ProgressCandidateData>(sql, ct);
        }

        public static async Task SaveSyncedProgressAsync(this LogicContext ctx, SyncedProgressData data, CancellationToken ct)
        {
            // Both carry the moment the candidates were read: a position saved in Jularr since then is neither overwritten nor taken as agreed.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "MediaProgress" ("ProfileId", "WorkId", "WorkUnitId", "ProgressPositionTypeId", "PositionMs", "DurationMs", "IsCompleted", "CompletedAt", "UpdatedAt")
                SELECT
                    @ProfileId,
                    item."WorkId",
                    item."WorkUnitId",
                    1,
                    item."PositionMs",
                    item."DurationMs",
                    item."IsCompleted",
                    CASE WHEN item."IsCompleted" THEN now() END,
                    @ReadAt
                FROM
                    jsonb_to_recordset(@Local::jsonb) AS item("WorkId" bigint, "WorkUnitId" bigint, "PositionMs" bigint, "DurationMs" bigint, "IsCompleted" boolean)
                ON CONFLICT ("ProfileId", "WorkId", "WorkUnitId") DO UPDATE
                SET
                    "PositionMs" = EXCLUDED."PositionMs",
                    "DurationMs" = coalesce(EXCLUDED."DurationMs", "MediaProgress"."DurationMs"),
                    "IsCompleted" = EXCLUDED."IsCompleted",
                    "CompletedAt" = CASE WHEN EXCLUDED."IsCompleted" THEN coalesce("MediaProgress"."CompletedAt", now()) END,
                    "UpdatedAt" = EXCLUDED."UpdatedAt"
                WHERE
                    "MediaProgress"."UpdatedAt" <= EXCLUDED."UpdatedAt";

                INSERT INTO
                    "ProviderProgress" ("ProviderProfileLinkId", "ExternalItemId", "PositionMs", "IsCompleted", "SyncedAt")
                SELECT
                    @ProviderProfileLinkId,
                    item."ExternalItemId",
                    item."PositionMs",
                    item."IsCompleted",
                    @ReadAt
                FROM
                    jsonb_to_recordset(@States::jsonb) AS item("ExternalItemId" text, "PositionMs" bigint, "IsCompleted" boolean)
                ON CONFLICT ("ProviderProfileLinkId", "ExternalItemId") DO UPDATE
                SET
                    "PositionMs" = EXCLUDED."PositionMs",
                    "IsCompleted" = EXCLUDED."IsCompleted",
                    "SyncedAt" = EXCLUDED."SyncedAt";
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task CompleteMediaServerSyncAsync(this LogicContext ctx, ProviderParameters parameters, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "ProviderProfileLinks" AS link
                SET
                    "SyncedAt" = now()
                FROM
                    "Providers" AS provider
                WHERE
                    provider."Id" = link."ProviderId"
                    AND provider."Key" = @ProviderKey
                    AND link."IsConfirmed"
                    AND link."ProfileId" IS NOT NULL;
                """;
            sql.Parameters.Add(parameters);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);

            var provider = new SqlStatement();
            provider.Statement =
                """
                UPDATE
                    "Providers" AS provider
                SET
                    "SyncedAt" = now()
                WHERE
                    provider."Key" = @ProviderKey;
                """;
            provider.Parameters.Add(parameters);
            if (await ctx.ExecuteAsyncForAffectedRows(provider, ct) != 1)
            {
                throw new KeyNotFoundException("The media server provider is not registered.");
            }
        }

        // Films and episodes as ReadProgressCandidatesAsync reads them; an episode takes the ids of its series and needs its numbers.
        public static string ItemRows(IEnumerable<MediaServerItem> items, IReadOnlyDictionary<string, MediaServerItem> series) =>
            JsonSerializer.Serialize(items.Select(item => (Item: item, Owner: item.Kind == MediaServerItemKind.Episode ? series.GetValueOrDefault(item.SeriesId ?? "") : item))
                .Where(row => row.Owner is not null && (row.Item.Kind != MediaServerItemKind.Episode || row.Item.Season is not null && row.Item.Episode is not null))
                .Select(row => new
                {
                    Key = row.Item.Id,
                    MediaTypeId = (byte)(row.Item.Kind == MediaServerItemKind.Movie ? MediaType.Movie : MediaType.Series),
                    row.Owner!.TmdbId,
                    row.Owner.ImdbId,
                    row.Owner.TvdbId,
                    row.Item.Season,
                    row.Item.Episode,
                    PositionMs = Math.Min(row.Item.PositionMs, PlaybackFields.PositionMs.Maximum),
                    row.Item.IsPlayed,
                    DurationMs = row.Item.DurationMs <= PlaybackFields.DurationMs.Maximum ? row.Item.DurationMs : null
                }));

        // One target per Work or episode: when a server has it twice (two versions of a film), finishing and then the further position decide.
        public static IReadOnlyList<ProgressAction> Plan(IReadOnlyList<ProgressCandidateData> candidates)
        {
            var actions = new List<ProgressAction>();
            foreach (var group in candidates.GroupBy(candidate => (candidate.WorkId, candidate.WorkUnitId)))
            {
                var targets = group.Select(Merge).OfType<ProgressTarget>().ToArray();
                if (targets.Length == 0)
                {
                    continue;
                }

                var target = targets.OrderByDescending(item => item.IsCompleted).ThenByDescending(item => item.PositionMs).First();
                var first = group.First();
                var writesLocal = target != new ProgressTarget(first.LocalPositionMs ?? 0, first.LocalIsCompleted ?? false);
                actions.AddRange(group.Select((candidate, index) => new ProgressAction(candidate, target, writesLocal && index == 0,
                    target != new ProgressTarget(candidate.RemotePositionMs, candidate.RemoteIsCompleted))));
            }

            return actions;
        }

        // The side that moved since the last agreement wins. When both moved, a side that just finished wins,
        // otherwise completion and the further position; Jularr wins ties. Null: nothing moved.
        public static ProgressTarget? Merge(ProgressCandidateData item)
        {
            var remote = new ProgressTarget(item.RemotePositionMs, item.RemoteIsCompleted);
            var local = new ProgressTarget(item.LocalPositionMs ?? 0, item.LocalIsCompleted ?? false);
            var agreed = item.SyncedAt is null ? null : new ProgressTarget(item.SyncedPositionMs ?? 0, item.SyncedIsCompleted ?? false);
            var remoteChanged = agreed is null ? remote.PositionMs > 0 || remote.IsCompleted : remote != agreed;
            var localChanged = item.LocalUpdatedAt is { } updatedAt && (item.SyncedAt is not { } syncedAt || updatedAt > syncedAt);
            if (remoteChanged && localChanged)
            {
                return local.IsCompleted && agreed?.IsCompleted != true ? local
                    : remote.IsCompleted && agreed?.IsCompleted != true ? remote
                    : new ProgressTarget(Math.Max(local.PositionMs, remote.PositionMs), local.IsCompleted || remote.IsCompleted);
            }

            return remoteChanged ? remote : localChanged ? local : null;
        }
    }

    internal sealed record ProgressTarget(long PositionMs, bool IsCompleted);

    // What one server item needs: the target, and whether Jularr or the server still has to take it over.
    internal sealed record ProgressAction(ProgressCandidateData Candidate, ProgressTarget Target, bool WritesLocal, bool WritesRemote);
}
