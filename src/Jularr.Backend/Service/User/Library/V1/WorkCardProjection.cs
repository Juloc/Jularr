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
            visible."Title" AS "Title",
            work."ReleaseYear"::integer AS "ReleaseYear",
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
            tracks."AudioLanguages",
            tracks."SubtitleLanguages"
            """;

        // The joins every card needs; the profile is the one the query reads for.
        public const string Joins =
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
                        JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id" AND file."MissingSince" IS NULL AND NOT file."IsSubtitle"
                    WHERE
                        asset."WorkId" = work."Id"
                        AND asset."WorkUnitId" IS NOT DISTINCT FROM unitSet."UnitId"
                    LIMIT 1
                ) AS present ON TRUE
            ) AS availability
            CROSS JOIN LATERAL (
                SELECT
                    coalesce(array_agg(DISTINCT track."Language") FILTER (WHERE track."MediaTrackTypeId" = 2 AND track."Language" IS NOT NULL), '{}') AS "AudioLanguages",
                    coalesce(array_agg(DISTINCT track."Language") FILTER (WHERE track."MediaTrackTypeId" = 3 AND track."Language" IS NOT NULL), '{}') AS "SubtitleLanguages"
                FROM
                    "MediaAssets" AS asset
                    JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id" AND file."MissingSince" IS NULL
                    JOIN "MediaTracks" AS track ON track."StoredFileId" = file."Id"
                WHERE
                    asset."WorkId" = work."Id"
            ) AS tracks
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
