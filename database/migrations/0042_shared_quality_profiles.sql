-- Quality profiles shared by media types: a profile applies to the types it names, each type has exactly one default.
-- Custom formats and release-group ratings exist once for every type; a series remux carries the film name (Remux-1080p).
-- Every target keeps the profile it follows today: where the new defaults would choose another, the old one is written onto the target.

-- The types a profile can apply to, as the media-type bar names them, and the quality list each shares.
CREATE TABLE "AcquisitionMediaKinds" (
    "Id" smallint PRIMARY KEY,
    "Key" varchar(16) NOT NULL UNIQUE,
    "QualityFamilyId" smallint NOT NULL CHECK ("QualityFamilyId" BETWEEN 1 AND 5)
);
INSERT INTO "AcquisitionMediaKinds" ("Id", "Key", "QualityFamilyId") VALUES
    (1, 'Movie', 1), (2, 'Series', 1), (3, 'Anime', 1), (4, 'Book', 2), (5, 'Manga', 3), (6, 'LightNovel', 2), (7, 'Music', 4), (8, 'Game', 5);

CREATE TABLE "AcquisitionProfileMediaKinds" (
    "AcquisitionProfileId" bigint NOT NULL REFERENCES "AcquisitionProfiles" ("Id") ON DELETE CASCADE,
    "AcquisitionMediaKindId" smallint NOT NULL REFERENCES "AcquisitionMediaKinds" ("Id"),
    PRIMARY KEY ("AcquisitionProfileId", "AcquisitionMediaKindId")
);
CREATE INDEX "IX_AcquisitionProfileMediaKinds_Kind" ON "AcquisitionProfileMediaKinds" ("AcquisitionMediaKindId", "AcquisitionProfileId");

-- One default per type, and only a profile that applies to that type; a default cannot be deleted or lose its type.
CREATE TABLE "AcquisitionProfileDefaults" (
    "AcquisitionMediaKindId" smallint PRIMARY KEY REFERENCES "AcquisitionMediaKinds" ("Id"),
    "AcquisitionProfileId" bigint NOT NULL,
    FOREIGN KEY ("AcquisitionProfileId", "AcquisitionMediaKindId") REFERENCES "AcquisitionProfileMediaKinds" ("AcquisitionProfileId", "AcquisitionMediaKindId")
);
CREATE INDEX "IX_AcquisitionProfileDefaults_Profile" ON "AcquisitionProfileDefaults" ("AcquisitionProfileId");

-- The type of a Work for its quality profile: its subtype, else its type.
CREATE VIEW "WorkAcquisitionKinds" AS
SELECT
    work."Id" AS "WorkId",
    (CASE
        WHEN work."MediaSubtypeId" = 1 THEN 3
        WHEN work."MediaSubtypeId" = 2 THEN 5
        WHEN work."MediaSubtypeId" = 3 THEN 6
        WHEN work."MediaTypeId" = 1 THEN 1
        WHEN work."MediaTypeId" = 2 THEN 2
        WHEN work."MediaTypeId" = 3 THEN 4
        WHEN work."MediaTypeId" = 6 THEN 7
        ELSE 8
    END)::smallint AS "AcquisitionMediaKindId"
FROM
    "Works" AS work;

-- The profile of every target, as acquisition uses it: its own when that applies to the Work's type, else the type's default.
CREATE VIEW "WantedItemProfiles" AS
SELECT
    wanted."Id" AS "WantedItemId",
    kind."AcquisitionMediaKindId",
    coalesce(own."AcquisitionProfileId", standard."AcquisitionProfileId") AS "AcquisitionProfileId"
FROM
    "WantedItems" AS wanted
    JOIN "WorkAcquisitionKinds" AS kind ON kind."WorkId" = wanted."WorkId"
    LEFT JOIN "AcquisitionProfileMediaKinds" AS own ON own."AcquisitionProfileId" = wanted."AcquisitionProfileId"
        AND own."AcquisitionMediaKindId" = kind."AcquisitionMediaKindId"
    LEFT JOIN "AcquisitionProfileDefaults" AS standard ON standard."AcquisitionMediaKindId" = kind."AcquisitionMediaKindId";

-- The profile every target follows today, by the old rule.
CREATE TEMPORARY TABLE "PreviousTargetProfiles" ON COMMIT DROP AS
SELECT
    wanted."Id" AS "WantedItemId",
    wanted."AcquisitionProfileId" AS "OwnProfileId",
    kind."AcquisitionMediaKindId",
    (
        SELECT
            candidate."Id"
        FROM
            "AcquisitionProfiles" AS candidate
        WHERE
            candidate."MediaTypeId" = work."MediaTypeId"
            AND (candidate."Id" = wanted."AcquisitionProfileId"
                OR wanted."AcquisitionProfileId" IS NULL AND candidate."IsDefault"
                    AND (candidate."MediaSubtypeId" IS NULL OR candidate."MediaSubtypeId" = work."MediaSubtypeId"))
        ORDER BY
            candidate."MediaSubtypeId" IS NULL
        LIMIT 1
    ) AS "ProfileId"
FROM
    "WantedItems" AS wanted
    JOIN "Works" AS work ON work."Id" = wanted."WorkId"
    JOIN "WorkAcquisitionKinds" AS kind ON kind."WorkId" = work."Id";

-- Each profile applies to the type it was made for; an Anime profile of films or series to Anime.
INSERT INTO "AcquisitionProfileMediaKinds" ("AcquisitionProfileId", "AcquisitionMediaKindId")
SELECT
    profile."Id",
    CASE
        WHEN profile."MediaSubtypeId" = 1 THEN 3
        WHEN profile."MediaSubtypeId" = 2 THEN 5
        WHEN profile."MediaSubtypeId" = 3 THEN 6
        WHEN profile."MediaTypeId" = 1 THEN 1
        WHEN profile."MediaTypeId" = 2 THEN 2
        WHEN profile."MediaTypeId" = 3 THEN 4
        WHEN profile."MediaTypeId" = 6 THEN 7
        ELSE 8
    END
FROM
    "AcquisitionProfiles" AS profile;

-- A type's default: the profile most of its targets without one of their own follow, else its own default, else the default it fell back to.
CREATE TEMPORARY TABLE "NewProfileDefaults" ON COMMIT DROP AS
SELECT
    kind."Id" AS "AcquisitionMediaKindId",
    coalesce(
        (
            SELECT
                previous."ProfileId"
            FROM
                "PreviousTargetProfiles" AS previous
            WHERE
                previous."AcquisitionMediaKindId" = kind."Id"
                AND previous."OwnProfileId" IS NULL
                AND previous."ProfileId" IS NOT NULL
            GROUP BY
                previous."ProfileId"
            ORDER BY
                count(*) DESC,
                previous."ProfileId"
            LIMIT 1
        ),
        (
            SELECT
                profile."Id"
            FROM
                (VALUES (1, 1, 1, NULL), (2, 1, 2, NULL), (3, 1, 2, 1), (3, 2, 1, 1), (3, 3, 2, NULL), (3, 4, 1, NULL), (4, 1, 3, NULL),
                    (5, 1, 3, 2), (5, 2, 3, NULL), (6, 1, 3, 3), (6, 2, 3, NULL), (7, 1, 6, NULL), (8, 1, 7, NULL)) AS fallback ("KindId", "Rank", "MediaTypeId", "MediaSubtypeId")
                JOIN "AcquisitionProfiles" AS profile ON profile."IsDefault"
                    AND profile."MediaTypeId" = fallback."MediaTypeId"
                    AND profile."MediaSubtypeId" IS NOT DISTINCT FROM fallback."MediaSubtypeId"::smallint
            WHERE
                fallback."KindId" = kind."Id"
            ORDER BY
                fallback."Rank"
            LIMIT 1
        )
    ) AS "AcquisitionProfileId"
FROM
    "AcquisitionMediaKinds" AS kind;

-- Every profile applies to the types of the targets that follow it, so each target keeps it.
INSERT INTO "AcquisitionProfileMediaKinds" ("AcquisitionProfileId", "AcquisitionMediaKindId")
SELECT DISTINCT previous."ProfileId", previous."AcquisitionMediaKindId" FROM "PreviousTargetProfiles" AS previous WHERE previous."ProfileId" IS NOT NULL
UNION
SELECT standard."AcquisitionProfileId", standard."AcquisitionMediaKindId" FROM "NewProfileDefaults" AS standard WHERE standard."AcquisitionProfileId" IS NOT NULL
ON CONFLICT DO NOTHING;

INSERT INTO "AcquisitionProfileDefaults" ("AcquisitionMediaKindId", "AcquisitionProfileId")
SELECT standard."AcquisitionMediaKindId", standard."AcquisitionProfileId" FROM "NewProfileDefaults" AS standard WHERE standard."AcquisitionProfileId" IS NOT NULL;

-- A target whose type now has another default keeps its profile as its own.
UPDATE
    "WantedItems" AS wanted
SET
    "AcquisitionProfileId" = previous."ProfileId",
    "Version" = wanted."Version" + 1,
    "UpdatedAt" = now()
FROM
    "PreviousTargetProfiles" AS previous
    LEFT JOIN "AcquisitionProfileDefaults" AS standard ON standard."AcquisitionMediaKindId" = previous."AcquisitionMediaKindId"
WHERE
    wanted."Id" = previous."WantedItemId"
    AND previous."OwnProfileId" IS NULL
    AND previous."ProfileId" IS NOT NULL
    AND previous."ProfileId" IS DISTINCT FROM standard."AcquisitionProfileId";

-- One video list: a series remux takes the film name.
UPDATE "AcquisitionProfiles" AS profile
SET "Definition" = regexp_replace(profile."Definition"::text, '"Bluray-(1080|2160)p Remux"', '"Remux-\1p"', 'gi')::jsonb
WHERE profile."Definition"::text ~* '"Bluray-(1080|2160)p Remux"';
UPDATE "StoredFiles" SET "QualityKey" = regexp_replace("QualityKey", '^Bluray-(1080|2160)p Remux$', 'Remux-\1p', 'i') WHERE "QualityKey" ~* '^Bluray-(1080|2160)p Remux$';
UPDATE "AcquisitionReleases" SET "QualityKey" = regexp_replace("QualityKey", '^Bluray-(1080|2160)p Remux$', 'Remux-\1p', 'i') WHERE "QualityKey" ~* '^Bluray-(1080|2160)p Remux$';
UPDATE "ActivityEvents" SET "QualityKey" = regexp_replace("QualityKey", '^Bluray-(1080|2160)p Remux$', 'Remux-\1p', 'i') WHERE "QualityKey" ~* '^Bluray-(1080|2160)p Remux$';
UPDATE "QualityDefinitions" SET "Quality" = regexp_replace("Quality", '^Bluray-(1080|2160)p Remux$', 'Remux-\1p', 'i') WHERE "Quality" ~* '^Bluray-(1080|2160)p Remux$';

-- A series profile lists the film-only qualities too, last and not allowed; series releases never have them.
UPDATE "AcquisitionProfiles" AS profile
SET "Definition" = jsonb_set(profile."Definition", '{QualityOrder}', (profile."Definition" -> 'QualityOrder') || missing."Qualities")
FROM
    "AcquisitionProfiles" AS source
    CROSS JOIN LATERAL (
        SELECT
            jsonb_agg(film."Key" ORDER BY film."Position") AS "Qualities"
        FROM
            unnest(ARRAY['Remux-2160p', 'Bluray-2160p', 'WEBDL-2160p', 'WEBRip-2160p', 'HDTV-2160p', 'Remux-1080p', 'Bluray-1080p', 'WEBDL-1080p', 'WEBRip-1080p', 'HDTV-1080p',
                'Raw-HD', 'Bluray-720p', 'WEBDL-720p', 'WEBRip-720p', 'HDTV-720p', 'BR-DISK', 'Bluray-576p', 'Bluray-480p', 'WEBDL-480p', 'WEBRip-480p', 'DVD-R', 'DVD', 'SDTV',
                'DVDSCR', 'REGIONAL', 'TELECINE', 'TELESYNC', 'CAM', 'WORKPRINT', 'Unknown']) WITH ORDINALITY AS film ("Key", "Position")
        WHERE
            NOT EXISTS (
                SELECT 1 FROM jsonb_array_elements_text(source."Definition" -> 'QualityOrder') AS listed ("Key") WHERE lower(listed."Key") = lower(film."Key"))
            AND NOT EXISTS (
                SELECT 1
                FROM jsonb_array_elements(coalesce(source."Definition" -> 'QualityGroups', '[]'::jsonb)) AS grouped
                    CROSS JOIN jsonb_array_elements_text(grouped -> 'Qualities') AS member ("Key")
                WHERE lower(member."Key") = lower(film."Key"))
    ) AS missing
WHERE
    profile."Id" = source."Id"
    AND source."MediaTypeId" IN (1, 2)
    AND missing."Qualities" IS NOT NULL;

-- Custom formats once: identical ones (name, conditions, renaming) become the oldest; profiles keep their scores, files their results.
CREATE TEMPORARY TABLE "MergedCustomFormats" ON COMMIT DROP AS
SELECT
    format."Id" AS "FormatId",
    kept."Id" AS "KeptId",
    kept."Version" AS "KeptVersion"
FROM
    "CustomFormats" AS format
    JOIN LATERAL (
        SELECT
            candidate."Id",
            candidate."Version"
        FROM
            "CustomFormats" AS candidate
        WHERE
            lower(candidate."Name") = lower(format."Name")
            AND candidate."Conditions" = format."Conditions"
            AND candidate."IncludeWhenRenaming" = format."IncludeWhenRenaming"
        ORDER BY
            candidate."Id"
        LIMIT 1
    ) AS kept ON kept."Id" <> format."Id";

UPDATE "AcquisitionProfiles" AS profile
SET "Definition" = jsonb_set(profile."Definition", '{FormatScores}', coalesce((
    SELECT
        jsonb_agg(jsonb_build_object('FormatId', scored."FormatId", 'Score', scored."Score") ORDER BY scored."FormatId")
    FROM (
        SELECT DISTINCT ON (coalesce(merged."KeptId", (score ->> 'FormatId')::bigint))
            coalesce(merged."KeptId", (score ->> 'FormatId')::bigint) AS "FormatId",
            (score ->> 'Score')::integer AS "Score"
        FROM
            jsonb_array_elements(profile."Definition" -> 'FormatScores') AS score
            LEFT JOIN "MergedCustomFormats" AS merged ON merged."FormatId" = (score ->> 'FormatId')::bigint
        ORDER BY
            coalesce(merged."KeptId", (score ->> 'FormatId')::bigint),
            (score ->> 'Score')::integer DESC
    ) AS scored
), '[]'::jsonb))
WHERE EXISTS (
    SELECT 1
    FROM jsonb_array_elements(coalesce(profile."Definition" -> 'FormatScores', '[]'::jsonb)) AS score
        JOIN "MergedCustomFormats" AS merged ON merged."FormatId" = (score ->> 'FormatId')::bigint
);

UPDATE "StoredFileCustomFormats" AS evaluated
SET "CustomFormatId" = merged."KeptId", "FormatVersion" = merged."KeptVersion"
FROM "MergedCustomFormats" AS merged
WHERE evaluated."CustomFormatId" = merged."FormatId"
    AND evaluated."FormatVersion" = (SELECT format."Version" FROM "CustomFormats" AS format WHERE format."Id" = merged."FormatId")
    AND NOT EXISTS (SELECT 1 FROM "StoredFileCustomFormats" AS other WHERE other."StoredFileId" = evaluated."StoredFileId" AND other."CustomFormatId" = merged."KeptId");
DELETE FROM "CustomFormats" AS format USING "MergedCustomFormats" AS merged WHERE format."Id" = merged."FormatId";

-- Formats of the same name that differ keep it once; the others name their type.
UPDATE "CustomFormats" AS format
SET "Name" = left(format."Name", 110) || ' (' || CASE format."MediaTypeId" WHEN 1 THEN 'Movies' WHEN 2 THEN 'Series' WHEN 3 THEN 'Books' WHEN 6 THEN 'Music' ELSE 'Games' END || ')',
    "Version" = format."Version" + 1
WHERE EXISTS (SELECT 1 FROM "CustomFormats" AS older WHERE lower(older."Name") = lower(format."Name") AND older."Id" < format."Id");

ALTER TABLE "CustomFormats" DROP COLUMN "MediaTypeId";
CREATE UNIQUE INDEX "UX_CustomFormats_Name" ON "CustomFormats" (lower("Name"));

-- Release groups once: the counts add up, the stricter rating wins (blocked over bad over good).
UPDATE "ReleaseGroups" AS kept
SET
    "SeenCount" = merged."SeenCount",
    "LastSeenAt" = merged."LastSeenAt",
    "ReleaseGroupRatingId" = merged."ReleaseGroupRatingId"
FROM (
    SELECT
        min(item."Id") AS "Id",
        sum(item."SeenCount")::integer AS "SeenCount",
        max(item."LastSeenAt") AS "LastSeenAt",
        max(item."ReleaseGroupRatingId") AS "ReleaseGroupRatingId"
    FROM
        "ReleaseGroups" AS item
    GROUP BY
        lower(item."Name")
    HAVING
        count(*) > 1
) AS merged
WHERE kept."Id" = merged."Id";
DELETE FROM "ReleaseGroups" AS item
WHERE EXISTS (SELECT 1 FROM "ReleaseGroups" AS older WHERE lower(older."Name") = lower(item."Name") AND older."Id" < item."Id");
DROP INDEX "ReleaseGroups_MediaTypeId_Name";
ALTER TABLE "ReleaseGroups" DROP COLUMN "MediaTypeId";
CREATE UNIQUE INDEX "UX_ReleaseGroups_Name" ON "ReleaseGroups" (lower("Name"));

-- The views that chose a profile by type read the one resolver.
CREATE OR REPLACE VIEW "StoredFileProfileScores" AS
SELECT evidence."StoredFileId", profile."Id" AS "AcquisitionProfileId",
    coalesce(sum((score ->> 'Score')::integer) FILTER (WHERE evaluated."Matches"
        AND evaluated."FormatVersion" = format."Version" AND evaluated."Evidence" = evidence."Evidence"), 0)::integer AS "FormatScore",
    bool_and(score IS NULL OR coalesce(evaluated."FormatVersion" = format."Version" AND evaluated."Evidence" = evidence."Evidence" AND evaluated."IsValid", FALSE)) AS "IsComplete"
FROM "StoredFileFormatEvidence" AS evidence
    JOIN "WorkAcquisitionKinds" AS kind ON kind."WorkId" = evidence."WorkId"
    JOIN "AcquisitionProfileMediaKinds" AS applies ON applies."AcquisitionMediaKindId" = kind."AcquisitionMediaKindId"
    JOIN "AcquisitionProfiles" AS profile ON profile."Id" = applies."AcquisitionProfileId"
    LEFT JOIN LATERAL jsonb_array_elements(coalesce(profile."Definition" -> 'FormatScores', '[]'::jsonb)) AS score ON TRUE
    LEFT JOIN "CustomFormats" AS format ON format."Id" = (score ->> 'FormatId')::bigint
    LEFT JOIN "StoredFileCustomFormats" AS evaluated ON evaluated."StoredFileId" = evidence."StoredFileId" AND evaluated."CustomFormatId" = format."Id"
GROUP BY evidence."StoredFileId", profile."Id";

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

ALTER TABLE "AcquisitionProfiles" DROP COLUMN "IsDefault", DROP COLUMN "MediaSubtypeId", DROP COLUMN "MediaTypeId";
