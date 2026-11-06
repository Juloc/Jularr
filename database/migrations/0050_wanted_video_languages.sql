-- The films and episodes whose present files state their audio but lack a first language of the target's chains: a release that has it may replace them (the feed offers it).
-- A file that states no audio (a library file without a release name, a work without an original language for an untagged name) covers every language: it is never searched for.
-- IsNoneCovered: none of the first languages is there (the Wanted list shows these); otherwise only some are (several users: a Multi release may still replace the file).
CREATE VIEW "WantedVideoLanguages" AS
WITH firsts AS MATERIALIZED (
    SELECT
        chain."WantedItemId",
        array_agg(DISTINCT chain."FirstLanguage"::text) AS "Firsts"
    FROM
        "WantedItemChains" AS chain
    GROUP BY
        chain."WantedItemId"
), coverage AS MATERIALIZED (
    -- Once: joined as it is, the view is evaluated again for every file.
    SELECT
        covered_unit."MediaAssetId",
        covered_unit."WorkUnitId"
    FROM
        "MediaAssetUnitCoverage" AS covered_unit
), present AS MATERIALIZED (
    SELECT
        wanted."Id" AS "WantedItemId",
        coverage."WorkUnitId",
        available."ReleaseLanguages"
    FROM
        "WantedItems" AS wanted
        JOIN "Works" AS work ON work."Id" = wanted."WorkId" AND work."MediaTypeId" IN (1, 2)
        JOIN "MediaAssets" AS asset ON asset."WorkId" = work."Id"
        JOIN "StoredFiles" AS available ON available."MediaAssetId" = asset."Id" AND available."MissingSince" IS NULL AND cardinality(available."ReleaseLanguages") > 0
        LEFT JOIN coverage ON coverage."MediaAssetId" = asset."Id"
        LEFT JOIN "WorkUnits" AS unit ON unit."Id" = coverage."WorkUnitId"
    WHERE
        unit."Id" IS NULL OR unit."IsMonitored"
), covered AS (
    SELECT
        present."WantedItemId",
        present."WorkUnitId",
        array_agg(DISTINCT language) AS "Languages"
    FROM
        present
        CROSS JOIN LATERAL unnest(present."ReleaseLanguages") AS language
    GROUP BY
        present."WantedItemId", present."WorkUnitId"
)
SELECT
    covered."WantedItemId",
    covered."WorkUnitId",
    NOT (firsts."Firsts" && covered."Languages"::text[]) AS "IsNoneCovered"
FROM
    covered
    JOIN firsts ON firsts."WantedItemId" = covered."WantedItemId"
    JOIN "WantedItemProfiles" AS resolved ON resolved."WantedItemId" = covered."WantedItemId"
    JOIN "AcquisitionProfiles" AS profile ON profile."Id" = resolved."AcquisitionProfileId"
WHERE
    (profile."Definition" ->> 'UpgradeAllowed')::boolean
    AND NOT (firsts."Firsts" <@ covered."Languages"::text[]);

-- The Wanted list's "below cutoff" also holds the titles none of whose first languages is there.
CREATE OR REPLACE VIEW "WantedBelowCutoff" AS
SELECT "WantedItemId" FROM "WantedUnitsBelowCutoff"
UNION
SELECT "WantedItemId" FROM "WantedVideoLanguages" WHERE "IsNoneCovered";
