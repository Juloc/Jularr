using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.Provider;
using Jularr.Backend.Infrastructure.Sql;

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
            await ctx.ExecuteAsyncForAffectedRows(update, ct);
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
                    )
                    AND (
                        @ProfileId::bigint IS NULL
                        OR EXISTS (
                            SELECT
                                1
                            FROM
                                "Profiles" AS profile
                            WHERE
                                profile."Id" = @ProfileId
                        )
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

        public static Task<IReadOnlyList<ProgressCandidateData>> ReadProgressCandidatesAsync(this LogicContext ctx, MediaServerItemsData data, CancellationToken ct)
        {
            // Items the profile may see, matched by provider id and for episodes by season and number; untouched ones are left out.
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    item."Id" AS "ExternalItemId",
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
                    state."SyncedAt"
                FROM
                    jsonb_to_recordset(@Items::jsonb) AS item(
                        "Id" text, "MediaTypeId" smallint, "TmdbNamespace" text, "TmdbId" text, "ImdbId" text, "TvdbId" text,
                        "Season" integer, "Episode" integer, "PositionMs" bigint, "IsPlayed" boolean, "DurationMs" bigint)
                    CROSS JOIN LATERAL (
                        SELECT
                            identity."WorkId"
                        FROM
                            "WorkExternalIdentities" AS identity
                            JOIN "Works" AS candidate ON candidate."Id" = identity."WorkId"
                            JOIN "ProfileWorks" AS visible ON visible."WorkId" = candidate."Id" AND visible."ProfileId" = @ProfileId
                        WHERE
                            candidate."MediaTypeId" = item."MediaTypeId"
                            AND (
                                identity."Namespace" = item."TmdbNamespace" AND identity."Value" = item."TmdbId"
                                OR identity."Namespace" = 'imdb' AND identity."Value" = item."ImdbId"
                                OR identity."Namespace" = 'tvdb' AND identity."Value" = item."TvdbId"
                            )
                        ORDER BY
                            identity."WorkId"
                        LIMIT 1
                    ) AS work
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
                    LEFT JOIN "ProviderProgress" AS state ON state."ProviderProfileLinkId" = @ProviderProfileLinkId AND state."ExternalItemId" = item."Id"
                WHERE
                    (item."Episode" IS NULL OR unit."Id" IS NOT NULL)
                    AND (item."PositionMs" > 0 OR item."IsPlayed" OR progress."Id" IS NOT NULL OR state."ExternalItemId" IS NOT NULL);
                """;
            sql.Parameters.Add(data);
            return ctx.ReadSql.ReadListAsync<ProgressCandidateData>(sql, ct);
        }

        public static async Task SaveSyncedProgressAsync(this LogicContext ctx, SyncedProgressData data, CancellationToken ct)
        {
            // Taken over as the server has it; the agreed state is written in the same moment, so it does not count as a local change.
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "MediaProgress" ("ProfileId", "WorkId", "WorkUnitId", "ProgressPositionTypeId", "PositionMs", "DurationMs", "IsCompleted", "CompletedAt")
                SELECT
                    @ProfileId,
                    item."WorkId",
                    item."WorkUnitId",
                    1,
                    item."PositionMs",
                    item."DurationMs",
                    item."IsCompleted",
                    CASE WHEN item."IsCompleted" THEN now() END
                FROM
                    jsonb_to_recordset(@Local::jsonb) AS item("WorkId" bigint, "WorkUnitId" bigint, "PositionMs" bigint, "DurationMs" bigint, "IsCompleted" boolean)
                ON CONFLICT ("ProfileId", "WorkId", "WorkUnitId") DO UPDATE
                SET
                    "PositionMs" = EXCLUDED."PositionMs",
                    "DurationMs" = coalesce(EXCLUDED."DurationMs", "MediaProgress"."DurationMs"),
                    "IsCompleted" = EXCLUDED."IsCompleted",
                    "CompletedAt" = CASE WHEN EXCLUDED."IsCompleted" THEN coalesce("MediaProgress"."CompletedAt", now()) END,
                    "UpdatedAt" = now();

                INSERT INTO
                    "ProviderProgress" ("ProviderProfileLinkId", "ExternalItemId", "PositionMs", "IsCompleted", "SyncedAt")
                SELECT
                    @ProviderProfileLinkId,
                    item."ExternalItemId",
                    item."PositionMs",
                    item."IsCompleted",
                    now()
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

                UPDATE
                    "Providers" AS provider
                SET
                    "SyncedAt" = now()
                WHERE
                    provider."Key" = @ProviderKey;
                """;
            sql.Parameters.Add(parameters);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // The side that moved since the last agreement wins. When both moved, a side that just finished wins,
        // otherwise completion and the further position; Jularr wins ties. Null: nothing to do.
        public static ProgressDecision? Merge(ProgressCandidateData item)
        {
            var remote = (Position: item.RemotePositionMs, Completed: item.RemoteIsCompleted);
            var local = (Position: item.LocalPositionMs ?? 0, Completed: item.LocalIsCompleted ?? false);
            var agreed = item.SyncedAt is null ? ((long Position, bool Completed)?)null : (item.SyncedPositionMs ?? 0, item.SyncedIsCompleted ?? false);
            var remoteChanged = agreed is { } before ? remote != before : remote.Position > 0 || remote.Completed;
            var localChanged = item.LocalUpdatedAt is { } updatedAt && (item.SyncedAt is not { } syncedAt || updatedAt > syncedAt);

            (long Position, bool Completed) target;
            if (remoteChanged && localChanged)
            {
                target = local.Completed && agreed?.Completed != true ? local
                    : remote.Completed && agreed?.Completed != true ? remote
                    : (Math.Max(local.Position, remote.Position), local.Completed || remote.Completed);
            }
            else if (remoteChanged)
            {
                target = remote;
            }
            else if (localChanged)
            {
                target = local;
            }
            else
            {
                return null;
            }

            return new ProgressDecision(target.Position, target.Completed, target != local, target != remote);
        }
    }

    internal sealed class ProgressDecision
    {
        public ProgressDecision(long positionMs, bool isCompleted, bool writesLocal, bool writesRemote)
        {
            PositionMs = positionMs;
            IsCompleted = isCompleted;
            WritesLocal = writesLocal;
            WritesRemote = writesRemote;
        }

        public long PositionMs { get; }
        public bool IsCompleted { get; }
        public bool WritesLocal { get; }
        public bool WritesRemote { get; }
    }
}
