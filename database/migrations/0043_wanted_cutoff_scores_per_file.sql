-- A file's format score is read for its own profile only. Joined to the whole "StoredFileProfileScores" view, PostgreSQL scored every file
-- for every profile before keeping the few it needed (seconds on the Wanted page); read through LATERAL (OFFSET 0) the view is filtered by file
-- and profile first. Same rows, same columns.
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
    AND NOT (array_agg(preferred."IsRequestedLanguage" ORDER BY quality."Position", scores."FormatScore" DESC, preferred."IsRequestedLanguage" DESC))[1];
