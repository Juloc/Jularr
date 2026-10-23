using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Service.User.Library.V1
{
    // The card of a Work. The library and the watchlist read the same projection, so both
    // lists show the same availability, the same real tracks and the same download state.
    // A query aliases "visible" (the profile's Work) and "work" (the Work itself).
    internal static class WorkCardProjection
    {
        // The columns every card carries.
        public const string Columns =
            """
            work."Id",
            work."MediaTypeId",
            visible."MediaSubtypeId",
            visible."Title" AS "Title",
            work."ReleaseYear"::integer AS "ReleaseYear",
            (
                -- The rating of the catalog entry the title came from; the best known one when several exist.
                SELECT
                    catalogEntry."Rating"
                FROM
                    "WorkExternalIdentities" AS identity
                    JOIN "ExternalWorks" AS catalogEntry ON catalogEntry."Namespace" = identity."Namespace" AND catalogEntry."Value" = identity."Value"
                WHERE
                    identity."WorkId" = work."Id"
                    AND catalogEntry."Rating" IS NOT NULL
                ORDER BY
                    catalogEntry."Votes" DESC NULLS LAST
                LIMIT 1
            ) AS "Rating",
            (
                SELECT
                    fact."Value"
                FROM
                    "WorkMetadataFacts" AS fact
                WHERE
                    fact."WorkId" = work."Id"
                    AND fact."FactKey" = 'Poster'
                ORDER BY
                    fact."IsManual" DESC
                LIMIT 1
            ) AS "PosterUrl",
            profileState."MediaStateTypeId",
            acquisition."AvailabilityStateId",
            availability."AvailableUnits",
            availability."MonitoredUnits",
            coalesce(tracks."AudioLanguages", '{}') AS "AudioLanguages",
            coalesce(tracks."SubtitleLanguages", '{}') AS "SubtitleLanguages",
            workProgress."ProgressStateId",
            workProgress."ProgressPercent"
            """;

        // Where the profile stands with the title: finished when its own progress or every unit is completed,
        // in progress when anything was started, else not started. Needs only the Work as "work" and the profile.
        private const string ProgressCounts =
            """
                SELECT
                    leaf."WorkId",
                    count(*)::integer AS "Total",
                    count(*) FILTER (WHERE unitProgress."IsCompleted")::integer AS "Done",
                    count(*) FILTER (WHERE NOT unitProgress."IsCompleted" AND (coalesce(unitProgress."PositionMs", 0) > 0 OR coalesce(unitProgress."Progression", 0) > 0))::integer AS "Started"
                FROM
                    "WorkUnits" AS leaf
                    LEFT JOIN "MediaProgress" AS unitProgress ON unitProgress."ProfileId" = @ProfileId AND unitProgress."WorkId" = leaf."WorkId" AND unitProgress."WorkUnitId" = leaf."Id"
                WHERE
                    NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "WorkUnits" AS child
                        WHERE
                            child."ParentWorkUnitId" = leaf."Id"
                    )
                GROUP BY
                    leaf."WorkId"
            """;

        public const string ProgressJoin = "LEFT JOIN (" + ProgressCounts + ") AS unitCounts ON unitCounts.\"WorkId\" = work.\"Id\"\n" + ProgressStateJoin;
        public const string PageProgressJoin = "LEFT JOIN \"UnitProgressCounts\" AS unitCounts ON unitCounts.\"WorkId\" = work.\"Id\"\n" + ProgressStateJoin;

        private const string ProgressStateJoin =
            """
            LEFT JOIN "MediaProgress" AS ownProgress ON ownProgress."ProfileId" = @ProfileId AND ownProgress."WorkId" = work."Id" AND ownProgress."WorkUnitId" IS NULL
            CROSS JOIN LATERAL (
                SELECT
                    state."ProgressStateId",
                    CASE
                        WHEN state."ProgressStateId" = 3 THEN 100
                        WHEN state."ProgressStateId" = 1 THEN 0
                        WHEN unitCounts."Total" > 0 THEN least(99, greatest(1, round(unitCounts."Done" * 100.0 / unitCounts."Total")))
                        WHEN ownProgress."Progression" IS NOT NULL THEN least(99, greatest(1, round(ownProgress."Progression" * 100)))
                        WHEN ownProgress."DurationMs" IS NOT NULL THEN least(99, greatest(1, round(coalesce(ownProgress."PositionMs", 0) * 100.0 / ownProgress."DurationMs")))
                        ELSE 1
                    END::integer AS "ProgressPercent"
                FROM
                    (
                        SELECT
                            CASE
                                WHEN coalesce(ownProgress."IsCompleted", FALSE) OR unitCounts."Total" > 0 AND unitCounts."Done" = unitCounts."Total" THEN 3
                                WHEN unitCounts."Done" > 0 OR unitCounts."Started" > 0 OR coalesce(ownProgress."PositionMs", 0) > 0 OR coalesce(ownProgress."Progression", 0) > 0 THEN 2
                                ELSE 1
                            END::smallint AS "ProgressStateId"
                    ) AS state
            ) AS workProgress
            """;

        // One ordered page: sorting and cards share the unit progress counts. Per-title availability runs for the selected Works alone.
        public static string Page(string candidates, string isWatchlisted) =>
            $$"""
            WITH "UnitProgressCounts" AS MATERIALIZED (
                {{ProgressCounts}}
            )
            SELECT
                {{Columns}},
                {{isWatchlisted}} AS "IsWatchlisted"
            FROM
                unnest(ARRAY({{candidates}})) WITH ORDINALITY AS picked ("Id", "Place")
                JOIN "Works" AS work ON work."Id" = picked."Id"
                JOIN LATERAL (
                    SELECT
                        profileWork."Title",
                        profileWork."MediaSubtypeId"
                    FROM
                        "ProfileWorks" AS profileWork
                    WHERE
                        profileWork."ProfileId" = @ProfileId
                        AND profileWork."WorkId" = work."Id"
                    -- Keep the profile view parameterized by the selected Work.
                    OFFSET 0
                ) AS visible ON TRUE
                {{CardJoins}}
                {{PageProgressJoin}}
            ORDER BY
                picked."Place";
            """;

        // The joins every card needs; the profile is the one the query reads for.
        public const string Joins = CardJoins + "\n" + ProgressJoin;

        private const string CardJoins =
            """
            JOIN "ProfileWorkStates" AS profileState ON profileState."ProfileId" = @ProfileId AND profileState."WorkId" = work."Id"
            LEFT JOIN LATERAL (
                -- The language of the profile's locale; the audio of a title is compared with it.
                SELECT
                    lower(split_part(locale."Locale", '-', 1)) AS "Language"
                FROM
                    "UiLocales" AS locale
                    JOIN "Profiles" AS profile ON profile."UiLocaleId" = locale."Id"
                WHERE
                    profile."Id" = @ProfileId
            ) AS profileLanguage ON TRUE
            CROSS JOIN LATERAL (
                SELECT
                    count(*)::integer AS "MonitoredUnits",
                    count(*) FILTER (WHERE present."Present" IS TRUE)::integer AS "AvailableUnits",
                    coalesce(bool_or(present."ProfileAudio" IS TRUE), FALSE) AS "HasProfileAudio",
                    coalesce(bool_or(present."ProfileAudio" IS FALSE), FALSE) AS "HasOtherAudio"
                FROM (
                    -- The units the profile monitors; a unit that groups others is no unit of its own.
                    SELECT
                        leaf."Id" AS "UnitId"
                    FROM
                        "WorkUnits" AS leaf
                    WHERE
                        leaf."WorkId" = work."Id"
                        AND leaf."IsMonitored"
                        AND NOT EXISTS (
                            SELECT
                                1
                            FROM
                                "WorkUnits" AS child
                            WHERE
                                child."ParentWorkUnitId" = leaf."Id"
                        )
                    UNION ALL
                    -- A Work without a monitored unit is the title itself.
                    SELECT
                        NULL::bigint
                    WHERE
                        NOT EXISTS (
                            SELECT
                                1
                            FROM
                                "WorkUnits" AS leaf
                            WHERE
                                leaf."WorkId" = work."Id"
                                AND leaf."IsMonitored"
                                AND NOT EXISTS (
                                    SELECT
                                        1
                                    FROM
                                        "WorkUnits" AS child
                                    WHERE
                                        child."ParentWorkUnitId" = leaf."Id"
                                )
                        )
                        AND EXISTS (
                            SELECT
                                1
                            FROM
                                "MediaAssets" AS asset
                            WHERE
                                asset."WorkId" = work."Id"
                        )
                ) AS unitSet
                LEFT JOIN LATERAL (
                    SELECT
                        TRUE AS "Present",
                        (
                            SELECT
                                bool_or(track."Language" = profileLanguage."Language")
                            FROM
                                "MediaTracks" AS track
                                JOIN "StoredFiles" AS file ON file."Id" = track."StoredFileId"
                            WHERE
                                file."MediaAssetId" = asset."Id"
                                AND track."MediaTrackTypeId" = 2
                                AND track."Language" IS NOT NULL
                        ) AS "ProfileAudio"
                    FROM
                        "MediaAssets" AS asset
                    WHERE
                        asset."WorkId" = work."Id"
                        AND (asset."WorkUnitId" = unitSet."UnitId" OR asset."WorkUnitId" IS NULL AND unitSet."UnitId" IS NULL)
                        AND EXISTS (
                            SELECT
                                1
                            FROM
                                "StoredFiles" AS file
                            WHERE
                                file."MediaAssetId" = asset."Id"
                                AND file."MissingSince" IS NULL
                                AND NOT file."IsSubtitle"
                            -- Probe the indexed asset instead of merging the file inventory per unit.
                            OFFSET 0
                        )
                    LIMIT 1
                ) AS present ON TRUE
            ) AS availability
            LEFT JOIN (
                SELECT
                    asset."WorkId",
                    coalesce(array_agg(DISTINCT track."Language") FILTER (WHERE track."MediaTrackTypeId" = 2 AND track."Language" IS NOT NULL), '{}') AS "AudioLanguages",
                    coalesce(array_agg(DISTINCT track."Language") FILTER (WHERE track."MediaTrackTypeId" = 3 AND track."Language" IS NOT NULL), '{}') AS "SubtitleLanguages"
                FROM
                    "MediaAssets" AS asset
                    JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id" AND file."MissingSince" IS NULL
                    JOIN "MediaTracks" AS track ON track."StoredFileId" = file."Id"
                GROUP BY
                    asset."WorkId"
            ) AS tracks ON tracks."WorkId" = work."Id"
            CROSS JOIN LATERAL (
                -- What happens with the title before the library holds it: a running download,
                -- a running search, a request that waits.
                SELECT
                    CASE
                        WHEN request."RequestStatus" IN (4, 5) OR downloading."IsDownloading" THEN 4
                        WHEN request."RequestStatus" = 3 THEN 5
                        WHEN request."RequestStatus" IN (1, 2) THEN 6
                        WHEN wanted."WantedItemId" IS NOT NULL THEN 5
                        WHEN availability."AvailableUnits" = 0 THEN 3
                        WHEN availability."AvailableUnits" < availability."MonitoredUnits" THEN
                            CASE WHEN availability."HasOtherAudio" THEN 9 ELSE 2 END
                        WHEN profileLanguage."Language" IS NULL THEN 1
                        WHEN availability."HasProfileAudio" THEN 7
                        WHEN availability."HasOtherAudio" THEN 8
                        ELSE 1
                    END::smallint AS "AvailabilityStateId"
                FROM
                    (SELECT 1) AS anchor
                    LEFT JOIN LATERAL (
                        SELECT
                            TRUE AS "IsDownloading"
                        FROM
                            "Operations" AS operation
                            JOIN "AcquisitionReleases" AS release ON release."Id" = operation."AcquisitionReleaseId"
                            JOIN "WantedItems" AS wanted ON wanted."Id" = release."WantedItemId"
                        WHERE
                            wanted."WorkId" = work."Id"
                            AND operation."OperationTypeId" = 2
                            AND operation."OperationStatusTypeId" IN (1, 2)
                        LIMIT 1
                    ) AS downloading ON TRUE
                    LEFT JOIN LATERAL (
                        SELECT
                            request."AcquisitionRequestStatusTypeId" AS "RequestStatus"
                        FROM
                            "AcquisitionRequests" AS request
                        WHERE
                            request."WorkId" = work."Id"
                            AND request."RequestedByProfileId" = @ProfileId
                            AND request."AcquisitionRequestStatusTypeId" BETWEEN 1 AND 5
                        ORDER BY
                            request."CreatedAt" DESC,
                            request."Id" DESC
                        LIMIT 1
                    ) AS request ON TRUE
                    LEFT JOIN LATERAL (
                        SELECT
                            wanted."Id" AS "WantedItemId"
                        FROM
                            "WantedItems" AS wanted
                        WHERE
                            wanted."WorkId" = work."Id"
                        LIMIT 1
                    ) AS wanted ON TRUE
            ) AS acquisition
            """;
    }
}
