-- A book is wanted once per language: the languages its requests ask for, or, for a Work nobody requested, the first language of every profile. Each language is covered by its own file.
-- The language a grab was made for stays on the release, so a release that names no language (found by its title, or the last resort) still covers the language it was taken for.
ALTER TABLE "AcquisitionReleases"
    ADD COLUMN IF NOT EXISTS "TargetLanguage" varchar(2) NULL CHECK ("TargetLanguage" ~ '^[a-z]{2}$');

-- The languages of a book target, in the order of the first request (requests) or of the profile (profiles), and whether a present file covers each.
-- A file covers the languages it names (or its release was taken for); a file that names none covers the first language still uncovered, one language per file,
-- so a library of books without language evidence is not searched again.
CREATE VIEW "WantedBookLanguages" AS
-- The books among the targets: one lookup of each target's Work (a join on the Works would scan the whole catalog when the books are few and early in it).
WITH books AS MATERIALIZED (
    SELECT
        wanted."Id" AS "WantedItemId",
        wanted."WorkId"
    FROM
        "WantedItems" AS wanted
    WHERE
        (SELECT work."MediaTypeId" = 3 AND work."MediaSubtypeId" IS DISTINCT FROM 2 FROM "Works" AS work WHERE work."Id" = wanted."WorkId")
), wanted_languages AS (
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
            books
            JOIN "RequestTargets" AS target ON target."WantedItemId" = books."WantedItemId"
            JOIN "AcquisitionRequests" AS request ON request."Id" = target."AcquisitionRequestId"
            JOIN "UiLocales" AS locale ON locale."Id" = request."UiLocaleId"
        WHERE
            request."AcquisitionRequestStatusTypeId" BETWEEN 2 AND 6
        GROUP BY
            target."WantedItemId", lower(split_part(locale."Locale", '-', 1))
        UNION ALL
        SELECT
            books."WantedItemId",
            lower(split_part(locale."Locale", '-', 1)),
            min(profile."Id")
        FROM
            books
            CROSS JOIN "Profiles" AS profile
            JOIN "UiLocales" AS locale ON locale."Id" = profile."UiLocaleId"
        WHERE
            NOT EXISTS (
                SELECT 1
                FROM "RequestTargets" AS target
                    JOIN "AcquisitionRequests" AS request ON request."Id" = target."AcquisitionRequestId"
                WHERE target."WantedItemId" = books."WantedItemId" AND request."AcquisitionRequestStatusTypeId" BETWEEN 2 AND 6
            )
        GROUP BY
            books."WantedItemId", lower(split_part(locale."Locale", '-', 1))
    ) AS requested
), present_files AS (
    SELECT
        books."WantedItemId",
        file."Id" AS "StoredFileId",
        coalesce(file."ReleaseLanguages", '{}'::varchar[]) AS "Languages"
    FROM
        books
        JOIN "MediaAssets" AS asset ON asset."WorkId" = books."WorkId"
        JOIN "StoredFiles" AS file ON file."MediaAssetId" = asset."Id" AND file."MissingSince" IS NULL AND NOT file."IsSubtitle"
    WHERE
        books."WantedItemId" IN (SELECT "WantedItemId" FROM wanted_languages)
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
