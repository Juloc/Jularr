-- A book is wanted once per language: the languages its requests ask for, or, for a Work nobody requested, the first language of every profile. Each language is covered by its own file.
-- The language a grab was made for stays on the release, so a release that names no language (found by its title, or the last resort) still covers the language it was taken for.
ALTER TABLE "AcquisitionReleases"
    ADD COLUMN IF NOT EXISTS "TargetLanguage" varchar(2) NULL CHECK ("TargetLanguage" ~ '^[a-z]{2}$');

-- The languages of a book target, in the order of the first request (requests) or of the profile (profiles), and whether a present file covers each.
-- A file covers the languages it names (or its release was taken for); a file that names none covers the first language still uncovered, one language per file,
-- so a library of books without language evidence is not searched again.
CREATE VIEW "WantedBookLanguages" AS
WITH wanted_languages AS (
    SELECT
        requested."WantedItemId",
        requested."Language",
        row_number() OVER (PARTITION BY requested."WantedItemId" ORDER BY requested."FirstRequestId", requested."Language")::integer AS "Position"
    FROM (
        SELECT
            target."WantedItemId",
            lower(split_part(locale."Locale", '-', 1)) AS "Language",
            min(request."Id") AS "FirstRequestId"
        FROM
            "RequestTargets" AS target
            JOIN "AcquisitionRequests" AS request ON request."Id" = target."AcquisitionRequestId"
            JOIN "UiLocales" AS locale ON locale."Id" = request."UiLocaleId"
        WHERE
            request."AcquisitionRequestStatusTypeId" BETWEEN 2 AND 6
        GROUP BY
            target."WantedItemId", lower(split_part(locale."Locale", '-', 1))
        UNION ALL
        SELECT
            wanted."Id",
            lower(split_part(locale."Locale", '-', 1)),
            min(profile."Id")
        FROM
            "WantedItems" AS wanted
            JOIN "Works" AS work ON work."Id" = wanted."WorkId" AND work."MediaTypeId" = 3 AND work."MediaSubtypeId" IS DISTINCT FROM 2
            CROSS JOIN "Profiles" AS profile
            JOIN "UiLocales" AS locale ON locale."Id" = profile."UiLocaleId"
        WHERE
            NOT EXISTS (
                SELECT 1
                FROM "RequestTargets" AS target
                    JOIN "AcquisitionRequests" AS request ON request."Id" = target."AcquisitionRequestId"
                WHERE target."WantedItemId" = wanted."Id" AND request."AcquisitionRequestStatusTypeId" BETWEEN 2 AND 6
            )
        GROUP BY
            wanted."Id", lower(split_part(locale."Locale", '-', 1))
    ) AS requested
        JOIN "WantedItems" AS item ON item."Id" = requested."WantedItemId"
        JOIN "Works" AS work ON work."Id" = item."WorkId" AND work."MediaTypeId" = 3 AND work."MediaSubtypeId" IS DISTINCT FROM 2
), present_files AS (
    SELECT
        wanted."Id" AS "WantedItemId",
        file."Id" AS "StoredFileId",
        coalesce(file."ReleaseLanguages", '{}'::varchar[]) AS "Languages"
    FROM
        "WantedItems" AS wanted
        JOIN "MediaAssets" AS asset ON asset."WorkId" = wanted."WorkId"
        JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id" AND file."MissingSince" IS NULL AND NOT file."IsSubtitle"
    WHERE
        wanted."Id" IN (SELECT "WantedItemId" FROM wanted_languages)
), named AS (
    SELECT
        wanted_languages."WantedItemId",
        wanted_languages."Language",
        wanted_languages."Position",
        EXISTS (
            SELECT 1 FROM present_files AS present
            WHERE present."WantedItemId" = wanted_languages."WantedItemId" AND wanted_languages."Language" = ANY (present."Languages")
        )
        OR EXISTS (
            SELECT 1
            FROM "AcquisitionReleases" AS taken
                JOIN present_files AS present ON present."WantedItemId" = taken."WantedItemId"
                JOIN "StoredFiles" AS file ON file."Id" = present."StoredFileId" AND file."ReleaseName" = left(taken."Title", 500)
            WHERE taken."WantedItemId" = wanted_languages."WantedItemId" AND taken."TargetLanguage" = wanted_languages."Language"
        ) AS "IsNamed"
    FROM
        wanted_languages
), unnamed AS (
    SELECT
        present."WantedItemId",
        count(*)::integer AS "Files"
    FROM
        present_files AS present
    WHERE
        cardinality(present."Languages") = 0
        AND NOT EXISTS (
            SELECT 1
            FROM "AcquisitionReleases" AS taken
                JOIN "StoredFiles" AS file ON file."Id" = present."StoredFileId" AND file."ReleaseName" = left(taken."Title", 500)
            WHERE taken."WantedItemId" = present."WantedItemId" AND taken."TargetLanguage" IS NOT NULL
        )
    GROUP BY
        present."WantedItemId"
), ranked AS (
    SELECT
        named.*,
        CASE WHEN NOT named."IsNamed" THEN row_number() OVER (PARTITION BY named."WantedItemId" ORDER BY named."Position") END AS "UncoveredPosition"
    FROM
        named
)
SELECT
    ranked."WantedItemId",
    ranked."Language",
    ranked."Position",
    ranked."IsNamed" OR ranked."UncoveredPosition" <= coalesce(unnamed."Files", 0) AS "IsCovered"
FROM
    ranked
    LEFT JOIN unnamed ON unnamed."WantedItemId" = ranked."WantedItemId";

-- A book that lacks a language of its target is below its cutoff: the automatic search takes the missing language, as for a missing upgrade.
CREATE OR REPLACE VIEW "WantedUnitsBelowCutoff" AS
WITH present_assets AS MATERIALIZED (
    SELECT
        asset."WorkId",
        asset."WorkVersionId",
        coverage."WorkUnitId",
        available."Id" AS "StoredFileId",
        available."QualityKey",
        available."ReleaseLanguages"
    FROM
        "StoredFiles" AS available
        JOIN "MediaAssets" AS asset ON asset."Id" = available."MediaAssetId"
        LEFT JOIN "MediaAssetUnitCoverage" AS coverage ON coverage."MediaAssetId" = asset."Id"
    WHERE
        available."MissingSince" IS NULL
)
SELECT wanted."Id" AS "WantedItemId", asset."WorkUnitId"
FROM "WantedItems" AS wanted
    JOIN "Works" AS work ON work."Id" = wanted."WorkId"
    JOIN "WantedItemProfiles" AS resolved ON resolved."WantedItemId" = wanted."Id"
    JOIN "AcquisitionProfiles" AS profile ON profile."Id" = resolved."AcquisitionProfileId"
    JOIN present_assets AS asset ON asset."WorkId" = work."Id"
    JOIN jsonb_array_elements_text(profile."Definition" -> 'QualityOrder') WITH ORDINALITY AS quality ("Key", "Position")
        ON lower(quality."Key") = lower(asset."QualityKey")
        OR EXISTS (
            SELECT 1
            FROM jsonb_array_elements(coalesce(profile."Definition" -> 'QualityGroups', '[]'::jsonb)) AS grouped
                CROSS JOIN jsonb_array_elements_text(grouped -> 'Qualities') AS member ("Key")
            WHERE 'group:' || (grouped ->> 'Id') = quality."Key"
                AND lower(member."Key") = lower(asset."QualityKey")
        )
    LEFT JOIN "WorkUnits" AS unit ON unit."Id" = asset."WorkUnitId"
    LEFT JOIN LATERAL (
        SELECT score."FormatScore", score."IsComplete"
        FROM "StoredFileProfileScores" AS score
        WHERE score."StoredFileId" = asset."StoredFileId" AND score."AcquisitionProfileId" = profile."Id"
        OFFSET 0
    ) AS scores ON TRUE
    CROSS JOIN LATERAL (
        SELECT EXISTS (
            SELECT 1 FROM "RequestTargets" AS target
                JOIN "AcquisitionRequests" AS request ON request."Id" = target."AcquisitionRequestId"
                JOIN "UiLocales" AS locale ON locale."Id" = request."UiLocaleId"
            WHERE target."WantedItemId" = wanted."Id" AND request."AcquisitionRequestStatusTypeId" BETWEEN 2 AND 6
                AND lower(split_part(locale."Locale", '-', 1)) = ANY(asset."ReleaseLanguages")
        ) AS "IsRequestedLanguage"
    ) AS preferred
WHERE (profile."Definition" ->> 'UpgradeAllowed')::boolean
    AND (unit."Id" IS NULL OR unit."IsMonitored")
    AND (wanted."GamePlatformId" IS NULL OR EXISTS (
        SELECT 1 FROM "GameReleases" AS game
        WHERE game."WorkVersionId" = asset."WorkVersionId" AND game."GamePlatformId" = wanted."GamePlatformId"
    ))
GROUP BY wanted."Id", asset."WorkUnitId", profile."Definition", work."MediaTypeId", work."MediaSubtypeId"
HAVING min(quality."Position") > coalesce((
    SELECT cutoff."Position"
    FROM jsonb_array_elements_text(profile."Definition" -> 'QualityOrder') WITH ORDINALITY AS cutoff ("Key", "Position")
    WHERE lower(cutoff."Key") = lower(profile."Definition" ->> 'UpgradeCutoffQuality')
    LIMIT 1
), 1)
OR bool_and(coalesce(scores."IsComplete", FALSE))
    AND (array_agg(scores."FormatScore" ORDER BY quality."Position", scores."FormatScore" DESC))[1]
        < coalesce((profile."Definition" ->> 'CutoffFormatScore')::integer, 0)
OR work."MediaTypeId" = 3 AND work."MediaSubtypeId" IS DISTINCT FROM 2
    AND EXISTS (
        SELECT 1 FROM "RequestTargets" AS target
            JOIN "AcquisitionRequests" AS request ON request."Id" = target."AcquisitionRequestId"
        WHERE target."WantedItemId" = wanted."Id" AND request."AcquisitionRequestStatusTypeId" BETWEEN 2 AND 6
    )
    AND NOT (array_agg(preferred."IsRequestedLanguage" ORDER BY quality."Position", scores."FormatScore" DESC, preferred."IsRequestedLanguage" DESC))[1]
UNION
SELECT missing."WantedItemId", NULL::bigint
FROM "WantedBookLanguages" AS missing
WHERE NOT missing."IsCovered";
