-- Reduce inventory before profile quality expansion; preserve every file and the canonical quality rules.
CREATE OR REPLACE VIEW "WantedUnitsBelowCutoff" AS
WITH present_assets AS MATERIALIZED (
    SELECT
        asset."WorkId",
        asset."WorkVersionId",
        asset."WorkUnitId",
        available."Id" AS "StoredFileId",
        available."QualityKey",
        available."ReleaseLanguages"
    FROM
        "StoredFiles" AS available
        JOIN "MediaAssets" AS asset ON asset."Id" = available."MediaAssetId"
    WHERE
        available."MissingSince" IS NULL
)
SELECT wanted."Id" AS "WantedItemId", asset."WorkUnitId"
FROM "WantedItems" AS wanted
    JOIN "Works" AS work ON work."Id" = wanted."WorkId"
    JOIN LATERAL (
        SELECT candidate."Id", candidate."Definition"
        FROM "AcquisitionProfiles" AS candidate
        WHERE candidate."MediaTypeId" = work."MediaTypeId"
            AND (candidate."Id" = wanted."AcquisitionProfileId"
                OR wanted."AcquisitionProfileId" IS NULL AND candidate."IsDefault"
                    AND (candidate."MediaSubtypeId" IS NULL OR candidate."MediaSubtypeId" = work."MediaSubtypeId"))
        ORDER BY candidate."MediaSubtypeId" IS NULL
        LIMIT 1
    ) AS profile ON TRUE
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
    LEFT JOIN "StoredFileProfileScores" AS scores ON scores."StoredFileId" = asset."StoredFileId" AND scores."AcquisitionProfileId" = profile."Id"
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
