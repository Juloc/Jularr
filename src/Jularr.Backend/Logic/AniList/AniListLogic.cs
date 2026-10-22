using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Logic.AniList;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.AniList
{
    // A profile's own AniList account: its connection, the list as last read and the progress taken over from it or sent to it.
    internal static class AniListLogic
    {
        public static Task<AniListApplicationData?> ReadAniListApplicationAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    provider."IsEnabled",
                    provider."ExternalId" AS "ClientId",
                    provider."ProtectedSecret"
                FROM
                    "Providers" AS provider
                WHERE
                    provider."Key" = 'anilist';
                """;
            return ctx.ReadSql.ReadOptionalAsync<AniListApplicationData>(sql, ct);
        }

        public static async Task SaveAniListConnectionAsync(this LogicContext ctx, AniListConnectionSaveData data, CancellationToken ct)
        {
            // One AniList user belongs to one profile.
            var taken = new SqlStatement();
            taken.Statement =
                """
                SELECT
                    connection."ProfileId"
                FROM
                    "AniListConnections" AS connection
                WHERE
                    connection."ViewerId" = @ViewerId
                    AND connection."ProfileId" <> @ProfileId;
                """;
            taken.Parameters.Add(data);
            if (await ctx.ReadSql.ReadOptionalAsync<AniListProfileData>(taken, ct) is not null)
            {
                throw new DataValidationException(nameof(data.ViewerId), "duplicate_value");
            }

            // Another AniList user brings another list.
            var reset = new SqlStatement();
            reset.Statement =
                """
                DELETE FROM
                    "AniListEntries" AS entry
                USING
                    "AniListConnections" AS connection
                WHERE
                    connection."ProfileId" = entry."ProfileId"
                    AND entry."ProfileId" = @ProfileId
                    AND connection."ViewerId" <> @ViewerId;
                """;
            reset.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(reset, ct);

            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "AniListConnections" ("ProfileId", "ViewerId", "ViewerName", "ProtectedToken", "ExpiresAt")
                VALUES
                    (@ProfileId, @ViewerId, @ViewerName, @ProtectedToken, @ExpiresAt)
                ON CONFLICT ("ProfileId") DO UPDATE
                SET
                    "PushEnabledAt" = CASE WHEN "AniListConnections"."ViewerId" = EXCLUDED."ViewerId" THEN "AniListConnections"."PushEnabledAt" END,
                    "SyncedAt" = CASE WHEN "AniListConnections"."ViewerId" = EXCLUDED."ViewerId" THEN "AniListConnections"."SyncedAt" END,
                    "ViewerId" = EXCLUDED."ViewerId",
                    "ViewerName" = EXCLUDED."ViewerName",
                    "ProtectedToken" = EXCLUDED."ProtectedToken",
                    "ExpiresAt" = EXCLUDED."ExpiresAt",
                    "ConnectedAt" = now(),
                    "FailureCode" = NULL;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // The list goes with the connection; progress in Jularr stays.
        public static async Task DeleteAniListConnectionAsync(this LogicContext ctx, AniListProfileData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                DELETE FROM
                    "AniListConnections" AS connection
                WHERE
                    connection."ProfileId" = @ProfileId;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task SaveAniListPushAsync(this LogicContext ctx, AniListPushSaveData data, CancellationToken ct)
        {
            // Only progress made after sending was switched on is sent.
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AniListConnections" AS connection
                SET
                    "PushEnabledAt" = CASE WHEN @IsPushEnabled THEN coalesce(connection."PushEnabledAt", now()) END
                WHERE
                    connection."ProfileId" = @ProfileId;
                """;
            sql.Parameters.Add(data);
            if (await ctx.ExecuteAsyncForAffectedRows(sql, ct) != 1)
            {
                throw new KeyNotFoundException("The AniList account is not connected.");
            }
        }

        public static Task<IReadOnlyList<AniListSyncConnectionData>> ReadAniListConnectionsAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    connection."ProfileId",
                    connection."ViewerId",
                    connection."ProtectedToken",
                    connection."ExpiresAt",
                    connection."PushEnabledAt"
                FROM
                    "AniListConnections" AS connection
                ORDER BY
                    connection."ProfileId";
                """;
            return ctx.ReadSql.ReadListAsync<AniListSyncConnectionData>(sql, ct);
        }

        // The entries of a list as AniList has them now; an entry that left the list leaves here. What was taken over and sent stays as it was.
        public static async Task SaveAniListEntriesAsync(this LogicContext ctx, AniListEntriesSaveData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                WITH incoming AS (
                    SELECT
                        item."MediaId",
                        item."ListEntryId",
                        item."IsManga",
                        item."Title",
                        item."Status",
                        item."Progress",
                        item."ProgressVolumes",
                        item."Score",
                        item."TotalCount",
                        item."TotalVolumes",
                        item."UpdatedAt"
                    FROM
                        jsonb_to_recordset(@Entries::jsonb) AS item (
                            "MediaId" integer,
                            "ListEntryId" bigint,
                            "IsManga" boolean,
                            "Title" text,
                            "Status" text,
                            "Progress" integer,
                            "ProgressVolumes" integer,
                            "Score" smallint,
                            "TotalCount" integer,
                            "TotalVolumes" integer,
                            "UpdatedAt" timestamptz
                        )
                ),
                removed AS (
                    DELETE FROM
                        "AniListEntries" AS entry
                    WHERE
                        entry."ProfileId" = @ProfileId
                        AND NOT EXISTS (
                            SELECT
                                1
                            FROM
                                incoming
                            WHERE
                                incoming."MediaId" = entry."MediaId"
                        )
                )
                INSERT INTO
                    "AniListEntries" ("ProfileId", "MediaId", "ListEntryId", "IsManga", "Title", "Status", "Progress", "ProgressVolumes", "Score", "TotalCount", "TotalVolumes", "RemoteUpdatedAt")
                SELECT
                    @ProfileId,
                    incoming."MediaId",
                    incoming."ListEntryId",
                    incoming."IsManga",
                    incoming."Title",
                    incoming."Status",
                    incoming."Progress",
                    incoming."ProgressVolumes",
                    incoming."Score",
                    incoming."TotalCount",
                    incoming."TotalVolumes",
                    incoming."UpdatedAt"
                FROM
                    incoming
                ON CONFLICT ("ProfileId", "MediaId") DO UPDATE
                SET
                    "ListEntryId" = EXCLUDED."ListEntryId",
                    "IsManga" = EXCLUDED."IsManga",
                    "Title" = EXCLUDED."Title",
                    "Status" = EXCLUDED."Status",
                    "Progress" = EXCLUDED."Progress",
                    "ProgressVolumes" = EXCLUDED."ProgressVolumes",
                    "Score" = EXCLUDED."Score",
                    "TotalCount" = EXCLUDED."TotalCount",
                    "TotalVolumes" = EXCLUDED."TotalVolumes",
                    "RemoteUpdatedAt" = EXCLUDED."RemoteUpdatedAt";
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // Entries of a title in the library whose AniList state changed since it was taken over; the others wait as pending matches.
        public static Task<IReadOnlyList<AniListEntryData>> ReadAniListImportEntriesAsync(this LogicContext ctx, AniListProfileData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    entry."MediaId",
                    entry."ListEntryId",
                    entry."IsManga",
                    entry."Status",
                    entry."Progress",
                    entry."ProgressVolumes",
                    entry."TotalCount",
                    entry."TotalVolumes",
                    entry."AttemptCount"::integer AS "AttemptCount"
                FROM
                    "AniListEntries" AS entry
                WHERE
                    entry."ProfileId" = @ProfileId
                    AND (entry."ImportedUpdatedAt" IS NULL OR entry."ImportedUpdatedAt" < entry."RemoteUpdatedAt")
                    AND EXISTS (
                        SELECT
                            1
                        FROM
                            "AniListEntryUnits" AS covered
                        WHERE
                            covered."ProfileId" = entry."ProfileId"
                            AND covered."MediaId" = entry."MediaId"
                    )
                ORDER BY
                    entry."MediaId"
                LIMIT 100;
                """;
            sql.Parameters.Add(data);
            return ctx.ReadSql.ReadListAsync<AniListEntryData>(sql, ct);
        }

        // Entries the profile tracks whose progress in Jularr moved after sending was switched on and since they were last looked at.
        public static Task<IReadOnlyList<AniListEntryData>> ReadAniListPushEntriesAsync(this LogicContext ctx, AniListProfileData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    entry."MediaId",
                    entry."ListEntryId",
                    entry."IsManga",
                    entry."Status",
                    entry."Progress",
                    entry."ProgressVolumes",
                    entry."TotalCount",
                    entry."TotalVolumes",
                    entry."AttemptCount"::integer AS "AttemptCount"
                FROM
                    "AniListEntries" AS entry
                    JOIN "AniListConnections" AS connection ON connection."ProfileId" = entry."ProfileId"
                WHERE
                    entry."ProfileId" = @ProfileId
                    AND connection."PushEnabledAt" IS NOT NULL
                    AND entry."Status" IN ('CURRENT', 'REPEATING', 'PLANNING')
                    AND (entry."NextAttemptAt" IS NULL OR entry."NextAttemptAt" <= now())
                    AND EXISTS (
                        SELECT
                            1
                        FROM
                            "AniListEntryUnits" AS covered
                            JOIN "MediaProgress" AS progress ON progress."ProfileId" = covered."ProfileId"
                                AND progress."WorkId" = covered."WorkId"
                                AND progress."WorkUnitId" IS NOT DISTINCT FROM covered."WorkUnitId"
                        WHERE
                            covered."ProfileId" = entry."ProfileId"
                            AND covered."MediaId" = entry."MediaId"
                            AND progress."IsCompleted"
                            AND progress."UpdatedAt" >= greatest(connection."PushEnabledAt", coalesce(entry."PushCheckedAt", connection."PushEnabledAt"))
                    )
                ORDER BY
                    entry."MediaId"
                LIMIT 100;
                """;
            sql.Parameters.Add(data);
            return ctx.ReadSql.ReadListAsync<AniListEntryData>(sql, ct);
        }

        // The films, episodes and volumes entries cover, among the titles the profile may open and that are out, with what the profile finished.
        public static Task<IReadOnlyList<AniListUnitData>> ReadAniListUnitsAsync(this LogicContext ctx, AniListUnitsReadData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
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
                    jsonb_array_elements_text(@MediaIds::jsonb) AS wanted (id)
                    JOIN "AniListEntryUnits" AS covered ON covered."ProfileId" = @ProfileId AND covered."MediaId" = wanted.id::integer
                    JOIN "AniListEntries" AS entry ON entry."ProfileId" = covered."ProfileId" AND entry."MediaId" = covered."MediaId"
                    LEFT JOIN "WorkUnits" AS unit ON unit."Id" = covered."WorkUnitId"
                    LEFT JOIN "MediaProgress" AS progress ON progress."ProfileId" = covered."ProfileId"
                        AND progress."WorkId" = covered."WorkId"
                        AND progress."WorkUnitId" IS NOT DISTINCT FROM covered."WorkUnitId"
                WHERE
                    EXISTS (
                        SELECT
                            1
                        FROM
                            "ProfileWorks" AS visible
                        WHERE
                            visible."ProfileId" = @ProfileId
                            AND visible."WorkId" = covered."WorkId"
                    )
                    AND (
                        unit."Id" IS NULL
                        OR unit."ReleasedOn" IS NULL
                        OR unit."ReleasedOn" <= current_date
                        OR EXISTS (SELECT 1 FROM "MediaAssets" AS asset WHERE asset."WorkUnitId" = unit."Id")
                    )
                ORDER BY
                    covered."MediaId",
                    covered."Number";
                """;
            sql.Parameters.Add(data);
            return ctx.ReadSql.ReadListAsync<AniListUnitData>(sql, ct);
        }

        // Units AniList counts as done are finished here, never one taken back; every looked-at entry of a library title counts as taken over.
        public static async Task ApplyAniListImportAsync(this LogicContext ctx, AniListImportData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                INSERT INTO
                    "MediaProgress" ("ProfileId", "WorkId", "WorkUnitId", "ProgressPositionTypeId", "PositionMs", "Locator", "Progression", "IsCompleted", "CompletedAt")
                SELECT
                    @ProfileId,
                    item."WorkId",
                    item."WorkUnitId",
                    CASE WHEN item."IsVolume" THEN 2 ELSE 1 END,
                    CASE WHEN item."IsVolume" THEN NULL ELSE 0 END,
                    CASE WHEN item."IsVolume" THEN 's0/b0' END,
                    CASE WHEN item."IsVolume" THEN 1 END,
                    TRUE,
                    now()
                FROM
                    jsonb_to_recordset(@Completions::jsonb) AS item ("WorkId" bigint, "WorkUnitId" bigint, "IsVolume" boolean)
                WHERE
                    EXISTS (
                        SELECT
                            1
                        FROM
                            "ProfileWorks" AS visible
                        WHERE
                            visible."ProfileId" = @ProfileId
                            AND visible."WorkId" = item."WorkId"
                    )
                ON CONFLICT ("ProfileId", "WorkId", "WorkUnitId") DO UPDATE
                SET
                    "IsCompleted" = TRUE,
                    "CompletedAt" = coalesce("MediaProgress"."CompletedAt", now()),
                    "PositionMs" = CASE WHEN "MediaProgress"."ProgressPositionTypeId" = 1 THEN 0 ELSE "MediaProgress"."PositionMs" END,
                    "Progression" = CASE WHEN "MediaProgress"."ProgressPositionTypeId" = 2 THEN 1 ELSE "MediaProgress"."Progression" END,
                    "UpdatedAt" = now()
                WHERE
                    NOT "MediaProgress"."IsCompleted";

                UPDATE
                    "AniListEntries" AS entry
                SET
                    "ImportedUpdatedAt" = entry."RemoteUpdatedAt"
                WHERE
                    entry."ProfileId" = @ProfileId
                    AND entry."MediaId" IN (SELECT wanted.id::integer FROM jsonb_array_elements_text(@MediaIds::jsonb) AS wanted (id))
                    AND EXISTS (
                        SELECT
                            1
                        FROM
                            "AniListEntryUnits" AS covered
                        WHERE
                            covered."ProfileId" = entry."ProfileId"
                            AND covered."MediaId" = entry."MediaId"
                    );
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // Entries looked at for sending; only progress made after this moment is looked at again.
        public static async Task MarkAniListCheckedAsync(this LogicContext ctx, AniListCheckedData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AniListEntries" AS entry
                SET
                    "PushCheckedAt" = @CheckedAt
                WHERE
                    entry."ProfileId" = @ProfileId
                    AND entry."MediaId" IN (SELECT wanted.id::integer FROM jsonb_array_elements_text(@MediaIds::jsonb) AS wanted (id));
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        // AniList's answer is the entry's state now; the next read sees nothing new in it.
        public static async Task RecordAniListPushedAsync(this LogicContext ctx, AniListPushedData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AniListEntries" AS entry
                SET
                    "Progress" = @Progress,
                    "ProgressVolumes" = @ProgressVolumes,
                    "Status" = CASE WHEN entry."Status" = 'PLANNING' THEN 'CURRENT' ELSE entry."Status" END,
                    "RemoteUpdatedAt" = @UpdatedAt,
                    "ImportedUpdatedAt" = @UpdatedAt,
                    "AttemptCount" = 0,
                    "NextAttemptAt" = NULL,
                    "LastError" = NULL
                WHERE
                    entry."ProfileId" = @ProfileId
                    AND entry."MediaId" = @MediaId;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task RecordAniListFailedAsync(this LogicContext ctx, AniListFailedData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AniListEntries" AS entry
                SET
                    "AttemptCount" = least(entry."AttemptCount" + 1, 32000),
                    "NextAttemptAt" = @NextAttemptAt,
                    "LastError" = @ErrorCode
                WHERE
                    entry."ProfileId" = @ProfileId
                    AND entry."MediaId" = @MediaId;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }

        public static async Task CompleteAniListPassAsync(this LogicContext ctx, AniListPassData data, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                UPDATE
                    "AniListConnections" AS connection
                SET
                    "SyncedAt" = CASE WHEN @FailureCode::varchar IS NULL THEN now() ELSE connection."SyncedAt" END,
                    "FailureCode" = @FailureCode
                WHERE
                    connection."ProfileId" = @ProfileId;
                """;
            sql.Parameters.Add(data);
            await ctx.ExecuteAsyncForAffectedRows(sql, ct);
        }
    }
}
