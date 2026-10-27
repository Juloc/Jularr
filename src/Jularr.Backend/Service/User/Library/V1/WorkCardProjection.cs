using Jularr.Backend.Infrastructure.Sql;

namespace Jularr.Backend.Service.User.Library.V1
{
    // The card of a Work. The library and the watchlist read the same projection, so both
    // lists show the same availability, the same real tracks and the same download state.
    // A query aliases "visible" (the profile's Work) and "work" (the Work itself).
    internal static class WorkCardProjection
    {
        public const string Rating =
            """
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
            """;

        // The columns every card carries.
        public const string Columns =
            $$"""
            work."Id",
            work."MediaTypeId",
            visible."MediaSubtypeId",
            visible."Title" AS "Title",
            work."ReleaseYear"::integer AS "ReleaseYear",
            ({{Rating}}) AS "Rating",
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

        public const string CardJoins =
            """
            JOIN "ProfileWorkStates" AS profileState ON profileState."ProfileId" = @ProfileId AND profileState."WorkId" = work."Id"
            LEFT JOIN LATERAL (
                -- Content choices are independent of the interface and acquisition settings.
                SELECT
                    profile."FirstContentLanguage" AS "Language",
                    ARRAY[profile."FirstContentLanguage", profile."SecondContentLanguage"] AS "Languages"
                FROM
                    "Profiles" AS profile
                WHERE
                    profile."Id" = @ProfileId
            ) AS profileLanguage ON TRUE
            CROSS JOIN LATERAL (
                SELECT
                    count(*)::integer AS "MonitoredUnits",
                    count(*) FILTER (WHERE present."Present" IS TRUE)::integer AS "AvailableUnits",
                    coalesce(bool_and(present."HasPreferredLanguage") FILTER (WHERE present."Present"), FALSE) AS "HasProfileAudio",
                    coalesce(bool_or(present."HasKnownLanguage" AND NOT present."HasPreferredLanguage" AND NOT present."HasUnknownLanguage" AND NOT present."HasPreferredSubtitles"), FALSE) AS "HasOtherAudio",
                    coalesce(bool_or(present."HasKnownLanguage" AND NOT present."HasPreferredLanguage" AND NOT present."HasUnknownLanguage" AND present."HasPreferredSubtitles"), FALSE) AS "HasPreferredSubtitles"
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
                ) AS unitSet
                LEFT JOIN LATERAL (
                    SELECT
                        count(*) > 0 AS "Present",
                        coalesce(bool_or(language."HasPreferredLanguage"), FALSE) AS "HasPreferredLanguage",
                        coalesce(bool_or(language."HasKnownLanguage"), FALSE) AS "HasKnownLanguage",
                        coalesce(bool_or(NOT language."HasKnownLanguage"), FALSE) AS "HasUnknownLanguage",
                        coalesce(bool_or(subtitles."HasPreferredSubtitles"), FALSE) AS "HasPreferredSubtitles"
                    FROM (
                        SELECT
                            candidate."Id"
                        FROM
                            "MediaAssets" AS candidate
                        WHERE
                            unitSet."UnitId" IS NULL
                            AND candidate."WorkId" = work."Id"
                            AND candidate."WorkUnitId" IS NULL
                        UNION ALL
                        SELECT
                            coverage."MediaAssetId"
                        FROM
                            "MediaAssetUnitCoverage" AS coverage
                        WHERE
                            unitSet."UnitId" IS NOT NULL
                            AND coverage."WorkId" = work."Id"
                            AND coverage."WorkUnitId" = unitSet."UnitId"
                    ) AS covered
                        JOIN "MediaAssets" AS asset ON asset."Id" = covered."Id"
                        JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id" AND file."MissingSince" IS NULL AND NOT file."IsSubtitle"
                        JOIN "WorkVersions" AS version ON version."Id" = asset."WorkVersionId"
                        JOIN "WorkEditions" AS edition ON edition."Id" = version."WorkEditionId"
                        LEFT JOIN LATERAL (
                            SELECT
                                candidate."Language"
                            FROM
                                "ReadingDocuments" AS candidate
                            WHERE
                                asset."MediaAssetTypeId" IN (3, 4)
                                AND candidate."StoredFileId" = file."Id"
                                AND candidate."SourceSizeBytes" = file."SizeBytes"
                                AND candidate."SourceModifiedAt" = file."ModifiedAt"
                            ORDER BY
                                candidate."AnalyzerVersion" DESC,
                                candidate."Id" DESC
                            LIMIT 1
                        ) AS document ON TRUE
                        CROSS JOIN LATERAL (
                            SELECT
                                coalesce(bool_or(content."Language" = ANY(profileLanguage."Languages")), FALSE) AS "HasPreferredLanguage",
                                count(*) > 0 AS "HasKnownLanguage"
                            FROM (
                                SELECT
                                    @LanguageAliases::jsonb ->> lower(split_part(replace(track."Language", '_', '-'), '-', 1)) AS "Language"
                                FROM
                                    "MediaTracks" AS track
                                WHERE
                                    track."StoredFileId" = file."Id"
                                    AND track."MediaTrackTypeId" = 2
                                    AND asset."MediaAssetTypeId" IN (1, 2)
                                UNION ALL
                                SELECT
                                    @LanguageAliases::jsonb ->> lower(split_part(replace(coalesce(nullif(document."Language", 'und'), edition."LanguageCode"), '_', '-'), '-', 1))
                                WHERE
                                    asset."MediaAssetTypeId" IN (3, 4)
                            ) AS content
                            WHERE
                                profileLanguage."Language" IS NOT NULL
                                AND content."Language" IS NOT NULL
                        ) AS language
                        CROSS JOIN LATERAL (
                            SELECT CASE WHEN language."HasKnownLanguage" AND NOT language."HasPreferredLanguage" THEN EXISTS (
                                SELECT
                                    1
                                FROM
                                    "StoredFiles" AS subtitleFile
                                    JOIN "MediaTracks" AS track ON track."StoredFileId" = subtitleFile."Id" AND track."MediaTrackTypeId" = 3
                                WHERE
                                    subtitleFile."MediaAssetId" = asset."Id"
                                    AND subtitleFile."MissingSince" IS NULL
                                    AND NOT track."IsForced"
                                    AND (@LanguageAliases::jsonb ->> lower(split_part(replace(track."Language", '_', '-'), '-', 1))) = ANY(profileLanguage."Languages")
                            ) ELSE FALSE END AS "HasPreferredSubtitles"
                        ) AS subtitles
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
                        WHEN availability."AvailableUnits" = 0 THEN
                            CASE
                                WHEN downloading."IsDownloading" THEN 4
                                WHEN request."RequestStatus" IN (1, 2) THEN 6
                                WHEN wanted."IsPaused" IS FALSE THEN 5
                                WHEN wanted."IsPaused" IS TRUE THEN 12
                                WHEN request."RequestStatus" = 3 THEN 5
                                WHEN request."RequestStatus" BETWEEN 1 AND 5 THEN 6
                                ELSE 3
                            END
                        WHEN availability."AvailableUnits" < availability."MonitoredUnits" THEN
                            CASE
                                WHEN profileLanguage."Language" IS NULL THEN 2
                                WHEN availability."HasOtherAudio" THEN 9
                                WHEN availability."HasPreferredSubtitles" THEN 11
                                ELSE 2
                            END
                        WHEN profileLanguage."Language" IS NULL THEN 1
                        WHEN availability."HasOtherAudio" THEN 8
                        WHEN availability."HasPreferredSubtitles" THEN 10
                        WHEN availability."HasProfileAudio" THEN 7
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
                            bool_and(wanted."IsPaused") AS "IsPaused"
                        FROM
                            "WantedItems" AS wanted
                        WHERE
                            wanted."WorkId" = work."Id"
                    ) AS wanted ON TRUE
            ) AS acquisition
            """;
    }
}
