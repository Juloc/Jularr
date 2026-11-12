using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Logic.Acquisition
{
    // The Wanted list reads every target's state as the worker kept it (WantedItemStates, WantedBookLanguageStates). A change to anything the states read leaves
    // a request (database triggers, migration 0058); while one is open, or the states are from another day (an episode airs at midnight), the list works the
    // states out as before. So it is never behind, only slower until the worker has refreshed.
    internal static class WantedStateLogic
    {
        // Every target with its state worked out now. Share the cutoff set and keep file existence checks as indexed probes, including with sparse scan statistics.
        internal const string LiveState =
            """
                    WITH cutoff_state AS MATERIALIZED (
                        SELECT
                            below."WantedItemId"
                        FROM
                            "WantedBelowCutoff" AS below
                    ), language_state AS MATERIALIZED (
                        -- The films and series none of whose first languages their files have (the reason of their place below the cutoff).
                        SELECT DISTINCT
                            language."WantedItemId"
                        FROM
                            "WantedVideoLanguages" AS language
                        WHERE
                            language."IsNoneCovered"
                    )
                    SELECT
                        wanted."Id",
                        wanted."WorkId",
                        work."MediaTypeId",
                        work."MediaSubtypeId",
                        work."CanonicalTitle",
                        wanted."IsPaused",
                        wanted."WantedPriorityTypeId",
                        wanted."MonitorsNewUnits",
                        wanted."LastSearchedAt",
                        wanted."LastOutcomeKey",
                        wanted."AcquisitionProfileId",
                        wanted."LibraryRootId",
                        wanted."GamePlatformId",
                        wanted."Version",
                        EXISTS (
                            SELECT
                                1
                            FROM
                                "MediaAssets" AS asset
                            WHERE
                                asset."WorkId" = wanted."WorkId"
                                AND EXISTS (
                                    SELECT
                                        1
                                    FROM
                                        "StoredFiles" AS available
                                    WHERE
                                        available."MediaAssetId" = asset."Id"
                                        AND available."MissingSince" IS NULL
                                    OFFSET 0
                                )
                                AND (
                                    wanted."GamePlatformId" IS NULL
                                    OR EXISTS (
                                        SELECT
                                            1
                                        FROM
                                            "GameReleases" AS game
                                        WHERE
                                            game."WorkVersionId" = asset."WorkVersionId"
                                            AND game."GamePlatformId" = wanted."GamePlatformId"
                                    )
                                )
                        ) AND NOT EXISTS (
                            SELECT
                                1
                            FROM
                                "WorkUnits" AS unit
                            WHERE
                                unit."WorkId" = wanted."WorkId"
                                AND unit."IsMonitored" AND (unit."WorkUnitTypeId" = 2 OR unit."WorkUnitTypeId" = 3 AND work."MediaSubtypeId" = 2)
                                AND (unit."ReleasedOn" <= current_date OR unit."WorkUnitTypeId" = 3 AND unit."ReleasedOn" IS NULL)
                                AND NOT EXISTS (
                                    SELECT
                                        1
                                    FROM
                                        "MediaAssets" AS asset
                                    WHERE
                                        asset."WorkId" = unit."WorkId"
                                        AND asset."WorkUnitId" = unit."Id"
                                        AND EXISTS (
                                            SELECT
                                                1
                                            FROM
                                                "StoredFiles" AS available
                                            WHERE
                                                available."MediaAssetId" = asset."Id"
                                                AND available."MissingSince" IS NULL
                                            OFFSET 0
                                        )
                                )
                        ) AS "HasMedia",
                        wanted."Id" IN (
                            SELECT
                                cutoff_state."WantedItemId"
                            FROM
                                cutoff_state
                        ) AS "IsCutoffUnmet",
                        wanted."Id" IN (
                            SELECT
                                language_state."WantedItemId"
                            FROM
                                language_state
                        ) AS "IsLanguageOpen"
                    FROM
                        "WantedItems" AS wanted
                    INNER JOIN
                        "Works" AS work ON work."Id" = wanted."WorkId"
                    WHERE
                        cardinality(@MediaTypeIds::smallint[]) + cardinality(@MediaSubtypeIds::smallint[]) = 0
                        OR work."MediaSubtypeId" IS NULL AND work."MediaTypeId" = ANY(@MediaTypeIds::smallint[])
                        OR work."MediaSubtypeId" = ANY(@MediaSubtypeIds::smallint[])
            """;

        // The same columns from the kept states; a target the worker has not seen yet has none and is not listed (the list is not current then).
        internal const string StoredState =
            """
                    SELECT
                        wanted."Id",
                        wanted."WorkId",
                        work."MediaTypeId",
                        work."MediaSubtypeId",
                        work."CanonicalTitle",
                        wanted."IsPaused",
                        wanted."WantedPriorityTypeId",
                        wanted."MonitorsNewUnits",
                        wanted."LastSearchedAt",
                        wanted."LastOutcomeKey",
                        wanted."AcquisitionProfileId",
                        wanted."LibraryRootId",
                        wanted."GamePlatformId",
                        wanted."Version",
                        state."HasMedia",
                        state."IsCutoffUnmet",
                        state."IsLanguageOpen"
                    FROM
                        "WantedItems" AS wanted
                    INNER JOIN
                        "Works" AS work ON work."Id" = wanted."WorkId"
                    INNER JOIN
                        "WantedItemStates" AS state ON state."WantedItemId" = wanted."Id"
                    WHERE
                        cardinality(@MediaTypeIds::smallint[]) + cardinality(@MediaSubtypeIds::smallint[]) = 0
                        OR work."MediaSubtypeId" IS NULL AND work."MediaTypeId" = ANY(@MediaTypeIds::smallint[])
                        OR work."MediaSubtypeId" = ANY(@MediaSubtypeIds::smallint[])
            """;

        // Whether the kept states are those the views give now.
        public static async Task<bool> IsWantedStateCurrentAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    EXISTS (
                        SELECT
                            1
                        FROM
                            "WantedStateRefreshes" AS refreshed
                        WHERE
                            refreshed."RefreshedAt"::date = current_date
                    )
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "WantedStateRequests" AS request
                    );
                """;
            return (bool)(await ctx.ReadSql.ExecuteScalarAsync(sql, ct))!;
        }

        // Works every target's state out once and keeps what changed; the requests it saw leave with it. One statement, so the states, the requests it removes and
        // its time are of the same moment: a change committed later leaves its request. Refreshes wait for each other.
        public static async Task<int> RefreshWantedStatesAsync(this LogicContext ctx, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement = "SELECT pg_advisory_xact_lock(hashtextextended('wanted-states', 0));";
            await ctx.Sql.ExecuteScalarAsync(sql, ct);

            sql = new SqlStatement { DisableJit = true };
            sql.Statement = "WITH seen AS MATERIALIZED (SELECT request.\"Id\" FROM \"WantedStateRequests\" AS request), wanted_base AS MATERIALIZED (" + LiveState +
                """
                ), book_languages AS MATERIALIZED (
                    SELECT
                        language."WantedItemId",
                        language."Language",
                        language."IsCovered"
                    FROM
                        "WantedBookLanguages" AS language
                ), kept AS (
                    INSERT INTO "WantedItemStates" ("WantedItemId", "HasMedia", "IsCutoffUnmet", "IsLanguageOpen")
                    SELECT
                        base."Id",
                        base."HasMedia",
                        base."IsCutoffUnmet",
                        base."IsLanguageOpen"
                    FROM
                        wanted_base AS base
                    ON CONFLICT ("WantedItemId") DO UPDATE
                    SET
                        "HasMedia" = EXCLUDED."HasMedia",
                        "IsCutoffUnmet" = EXCLUDED."IsCutoffUnmet",
                        "IsLanguageOpen" = EXCLUDED."IsLanguageOpen"
                    WHERE
                        ("WantedItemStates"."HasMedia", "WantedItemStates"."IsCutoffUnmet", "WantedItemStates"."IsLanguageOpen")
                            IS DISTINCT FROM (EXCLUDED."HasMedia", EXCLUDED."IsCutoffUnmet", EXCLUDED."IsLanguageOpen")
                    RETURNING
                        1
                ), gone AS (
                    DELETE FROM
                        "WantedItemStates" AS state
                    WHERE
                        NOT EXISTS (SELECT 1 FROM wanted_base AS base WHERE base."Id" = state."WantedItemId")
                    RETURNING
                        1
                ), languages_kept AS (
                    INSERT INTO "WantedBookLanguageStates" ("WantedItemId", "Language", "IsCovered")
                    SELECT
                        language."WantedItemId",
                        language."Language",
                        language."IsCovered"
                    FROM
                        book_languages AS language
                    ON CONFLICT ("WantedItemId", "Language") DO UPDATE
                    SET
                        "IsCovered" = EXCLUDED."IsCovered"
                    WHERE
                        "WantedBookLanguageStates"."IsCovered" IS DISTINCT FROM EXCLUDED."IsCovered"
                    RETURNING
                        1
                ), languages_gone AS (
                    DELETE FROM
                        "WantedBookLanguageStates" AS state
                    WHERE
                        NOT EXISTS (
                            SELECT
                                1
                            FROM
                                book_languages AS language
                            WHERE
                                language."WantedItemId" = state."WantedItemId"
                                AND language."Language" = state."Language"
                        )
                    RETURNING
                        1
                ), answered AS (
                    DELETE FROM
                        "WantedStateRequests" AS request
                    WHERE
                        request."Id" IN (SELECT seen."Id" FROM seen)
                    RETURNING
                        1
                ), stamped AS (
                    INSERT INTO "WantedStateRefreshes" ("Id", "RefreshedAt")
                    VALUES (1, statement_timestamp())
                    ON CONFLICT ("Id") DO UPDATE
                    SET
                        "RefreshedAt" = EXCLUDED."RefreshedAt"
                    RETURNING
                        1
                )
                SELECT
                    ((SELECT count(*) FROM kept) + (SELECT count(*) FROM gone) + (SELECT count(*) FROM languages_kept) + (SELECT count(*) FROM languages_gone))::integer;
                """;
            sql.Parameters.Add(new WantedViewFilterData(WantedViewType.All, [], []));
            return (int)(await ctx.Sql.ExecuteScalarAsync(sql, ct))!;
        }
    }
}
