-- The language chain: each profile has a first language, an optional second and whether the original language is allowed (its content languages and a switch); titles without a request follow an administrator's
-- setting (every profile's first language, or one fixed chain), and a title can have a chain of its own. The profiles' language lists and fallbacks become these chains.

-- Every profile's chain: its content languages, the language of its interface where it has no first one.
CREATE VIEW "ProfileLanguages" AS
SELECT
    profile."Id" AS "ProfileId",
    coalesce(profile."FirstContentLanguage", CASE WHEN lower(split_part(locale."Locale", '-', 1)) ~ '^[a-z]{2}$' THEN lower(split_part(locale."Locale", '-', 1)) ELSE 'en' END)::varchar(2) AS "FirstLanguage",
    profile."SecondContentLanguage" AS "SecondLanguage",
    profile."LanguageUsesOriginal" AS "UsesOriginal"
FROM "Profiles" AS profile
    JOIN "UiLocales" AS locale ON locale."Id" = profile."UiLocaleId";

-- 1 every profile's first language, 2 one fixed chain; for titles nobody requested.
ALTER TABLE "InstanceSettings"
    ADD COLUMN IF NOT EXISTS "MonitoredLanguageModeId" smallint NOT NULL DEFAULT 1 CHECK ("MonitoredLanguageModeId" IN (1, 2)),
    ADD COLUMN IF NOT EXISTS "MonitoredFirstLanguage" varchar(2) NULL CHECK ("MonitoredFirstLanguage" ~ '^[a-z]{2}$'),
    ADD COLUMN IF NOT EXISTS "MonitoredSecondLanguage" varchar(2) NULL CHECK ("MonitoredSecondLanguage" ~ '^[a-z]{2}$' AND "MonitoredSecondLanguage" <> "MonitoredFirstLanguage"),
    ADD COLUMN IF NOT EXISTS "MonitoredUsesOriginal" boolean NOT NULL DEFAULT TRUE;
UPDATE "InstanceSettings" AS settings
SET "MonitoredFirstLanguage" = CASE WHEN lower(split_part(locale."Locale", '-', 1)) ~ '^[a-z]{2}$' THEN lower(split_part(locale."Locale", '-', 1)) ELSE 'en' END
FROM "UiLocales" AS locale WHERE locale."Id" = settings."UiLocaleId";

-- A title's own chain, an administrator's choice; it replaces every other source for that title.
CREATE TABLE "WantedItemLanguageChains" (
    "WantedItemId" bigint PRIMARY KEY REFERENCES "WantedItems" ("Id") ON DELETE CASCADE,
    "FirstLanguage" varchar(2) NOT NULL CHECK ("FirstLanguage" ~ '^[a-z]{2}$'),
    "SecondLanguage" varchar(2) NULL CHECK ("SecondLanguage" ~ '^[a-z]{2}$' AND "SecondLanguage" <> "FirstLanguage"),
    "UsesOriginal" boolean NOT NULL DEFAULT TRUE
);

-- What each target followed before: a book's profile named languages and a fallback, else the languages of its requests. Where the new rule would choose another chain
-- (a profile with its own list, or a fallback that is English or none), the old one is written onto the target, so nothing is searched differently after the update.
INSERT INTO "WantedItemLanguageChains" ("WantedItemId", "FirstLanguage", "SecondLanguage", "UsesOriginal")
SELECT old."WantedItemId", old."First", CASE WHEN old."Second" IS DISTINCT FROM old."First" THEN old."Second" END, old."UsesOriginal"
FROM (
    SELECT
        wanted."Id" AS "WantedItemId",
        coalesce(lower(profile."Definition" -> 'LanguageOrder' ->> 0), requested."First") AS "First",
        coalesce(nullif(lower(profile."Definition" -> 'LanguageOrder' ->> 1), ''), CASE WHEN lower(profile."Definition" ->> 'LanguageFallback') IN ('2', 'english') THEN 'en' END) AS "Second",
        coalesce(lower(profile."Definition" ->> 'LanguageFallback'), 'any') NOT IN ('3', 'none', '2', 'english') AS "UsesOriginal",
        jsonb_array_length(coalesce(profile."Definition" -> 'LanguageOrder', '[]'::jsonb)) > 0 AS "HasOrder",
        coalesce(lower(profile."Definition" ->> 'LanguageFallback'), 'any') IN ('3', 'none', '2', 'english') AS "HasFallback"
    FROM "WantedItems" AS wanted
        JOIN "Works" AS work ON work."Id" = wanted."WorkId" AND work."MediaTypeId" = 3 AND work."MediaSubtypeId" IS DISTINCT FROM 2
        JOIN "WantedItemProfiles" AS resolved ON resolved."WantedItemId" = wanted."Id"
        JOIN "AcquisitionProfiles" AS profile ON profile."Id" = resolved."AcquisitionProfileId"
        LEFT JOIN LATERAL (
            SELECT lower(split_part(locale."Locale", '-', 1)) AS "First"
            FROM "RequestTargets" AS target
                JOIN "AcquisitionRequests" AS request ON request."Id" = target."AcquisitionRequestId" AND request."AcquisitionRequestStatusTypeId" BETWEEN 2 AND 6
                JOIN "UiLocales" AS locale ON locale."Id" = request."UiLocaleId"
            WHERE target."WantedItemId" = wanted."Id"
            ORDER BY request."Id"
            LIMIT 1
        ) AS requested ON TRUE
) AS old
WHERE (old."HasOrder" OR old."HasFallback") AND old."First" ~ '^[a-z]{2}$';

-- The profiles keep their qualities only.
UPDATE "AcquisitionProfiles" SET "Definition" = "Definition" - 'LanguageOrder' - 'LanguageFallback', "Version" = "Version" + 1
WHERE "Definition" ? 'LanguageOrder' OR "Definition" ? 'LanguageFallback';

-- A profile that now equals the default of every type it applies to had only its languages to say: its targets, requests and rules follow the defaults, and it goes.
CREATE TEMPORARY TABLE "LanguageProfiles" ON COMMIT DROP AS
SELECT profile."Id" AS "ProfileId",
    (SELECT standard."AcquisitionProfileId" FROM "AcquisitionProfileMediaKinds" AS applies
        JOIN "AcquisitionProfileDefaults" AS standard ON standard."AcquisitionMediaKindId" = applies."AcquisitionMediaKindId"
        WHERE applies."AcquisitionProfileId" = profile."Id" ORDER BY applies."AcquisitionMediaKindId" LIMIT 1) AS "KeptId"
FROM "AcquisitionProfiles" AS profile
WHERE NOT EXISTS (SELECT 1 FROM "AcquisitionProfileDefaults" AS standard WHERE standard."AcquisitionProfileId" = profile."Id")
    AND EXISTS (SELECT 1 FROM "AcquisitionProfileMediaKinds" AS applies WHERE applies."AcquisitionProfileId" = profile."Id")
    AND NOT EXISTS (
        SELECT 1 FROM "AcquisitionProfileMediaKinds" AS applies
            JOIN "AcquisitionProfileDefaults" AS standard ON standard."AcquisitionMediaKindId" = applies."AcquisitionMediaKindId"
            JOIN "AcquisitionProfiles" AS standardProfile ON standardProfile."Id" = standard."AcquisitionProfileId"
        WHERE applies."AcquisitionProfileId" = profile."Id" AND standardProfile."Definition" IS DISTINCT FROM profile."Definition");
UPDATE "WantedItems" AS wanted SET "AcquisitionProfileId" = NULL, "Version" = wanted."Version" + 1, "UpdatedAt" = now()
FROM "LanguageProfiles" AS merged WHERE wanted."AcquisitionProfileId" = merged."ProfileId";
UPDATE "AcquisitionRequests" AS request SET "AcquisitionProfileId" = NULL FROM "LanguageProfiles" AS merged WHERE request."AcquisitionProfileId" = merged."ProfileId";
UPDATE "AcquisitionProfileImports" AS imported SET "AcquisitionProfileId" = merged."KeptId" FROM "LanguageProfiles" AS merged WHERE imported."AcquisitionProfileId" = merged."ProfileId";
UPDATE "RequestRules" AS rule
SET "AcquisitionProfileIds" = ARRAY(SELECT item FROM unnest(rule."AcquisitionProfileIds") AS item WHERE item <> ALL (ARRAY(SELECT "ProfileId" FROM "LanguageProfiles")) ORDER BY 1),
    "Version" = rule."Version" + 1
WHERE rule."AcquisitionProfileIds" && ARRAY(SELECT "ProfileId" FROM "LanguageProfiles");
UPDATE "AccountRequestRules" AS rule
SET "AcquisitionProfileIds" = ARRAY(SELECT item FROM unnest(rule."AcquisitionProfileIds") AS item WHERE item <> ALL (ARRAY(SELECT "ProfileId" FROM "LanguageProfiles")) ORDER BY 1)
WHERE rule."AcquisitionProfileIds" && ARRAY(SELECT "ProfileId" FROM "LanguageProfiles");
DO $$
DECLARE
    merged record;
BEGIN
    FOR merged IN SELECT "ProfileId", "KeptId" FROM "LanguageProfiles" LOOP
        UPDATE "MediaImportJobs"
        SET "Payload" = regexp_replace("Payload"::text, '("AcquisitionProfileId\\?": ?)' || merged."ProfileId" || '(?![0-9])', '\1' || merged."KeptId", 'g')::jsonb
        WHERE "Payload"::text ~ ('"AcquisitionProfileId\\?": ?' || merged."ProfileId" || '(?![0-9])');
    END LOOP;
END $$;
DELETE FROM "AcquisitionProfiles" AS profile USING "LanguageProfiles" AS merged WHERE profile."Id" = merged."ProfileId";

-- The chains of a target, one per language it is wanted in, in order: its own, else those of its requests (the requester's current content languages and switch, the language of the
-- request where the requester has no first content language or is gone), else the setting for titles nobody requested.
CREATE VIEW "WantedItemChains" AS
WITH sources AS (
    SELECT own."WantedItemId", own."FirstLanguage", own."SecondLanguage", own."UsesOriginal", 0::bigint AS "Rank"
    FROM "WantedItemLanguageChains" AS own
    UNION ALL
    SELECT target."WantedItemId", coalesce(requester."FirstContentLanguage", lower(split_part(locale."Locale", '-', 1)))::varchar(2), requester."SecondContentLanguage",
        coalesce(requester."LanguageUsesOriginal", TRUE), request."Id"
    FROM "RequestTargets" AS target
        JOIN "AcquisitionRequests" AS request ON request."Id" = target."AcquisitionRequestId" AND request."AcquisitionRequestStatusTypeId" BETWEEN 2 AND 6
        JOIN "UiLocales" AS locale ON locale."Id" = request."UiLocaleId"
        LEFT JOIN "Profiles" AS requester ON requester."Id" = request."RequestedByProfileId"
    WHERE NOT EXISTS (SELECT 1 FROM "WantedItemLanguageChains" AS own WHERE own."WantedItemId" = target."WantedItemId")
    UNION ALL
    SELECT wanted."Id", chain."FirstLanguage", chain."SecondLanguage", chain."UsesOriginal", chain."ProfileId"
    FROM "WantedItems" AS wanted
        CROSS JOIN "InstanceSettings" AS settings
        CROSS JOIN "ProfileLanguages" AS chain
    WHERE settings."MonitoredLanguageModeId" = 1
        AND NOT EXISTS (SELECT 1 FROM "WantedItemLanguageChains" AS own WHERE own."WantedItemId" = wanted."Id")
        AND NOT EXISTS (
            SELECT 1 FROM "RequestTargets" AS target
                JOIN "AcquisitionRequests" AS request ON request."Id" = target."AcquisitionRequestId" AND request."AcquisitionRequestStatusTypeId" BETWEEN 2 AND 6
            WHERE target."WantedItemId" = wanted."Id")
    UNION ALL
    SELECT wanted."Id", settings."MonitoredFirstLanguage", settings."MonitoredSecondLanguage", settings."MonitoredUsesOriginal", 0
    FROM "WantedItems" AS wanted
        CROSS JOIN "InstanceSettings" AS settings
    WHERE settings."MonitoredLanguageModeId" = 2 AND settings."MonitoredFirstLanguage" IS NOT NULL
        AND NOT EXISTS (SELECT 1 FROM "WantedItemLanguageChains" AS own WHERE own."WantedItemId" = wanted."Id")
        AND NOT EXISTS (
            SELECT 1 FROM "RequestTargets" AS target
                JOIN "AcquisitionRequests" AS request ON request."Id" = target."AcquisitionRequestId" AND request."AcquisitionRequestStatusTypeId" BETWEEN 2 AND 6
            WHERE target."WantedItemId" = wanted."Id")
), first_of_each AS (
    SELECT DISTINCT ON (sources."WantedItemId", sources."FirstLanguage") sources.*
    FROM sources
    ORDER BY sources."WantedItemId", sources."FirstLanguage", sources."Rank"
)
SELECT
    first_of_each."WantedItemId",
    first_of_each."FirstLanguage",
    first_of_each."SecondLanguage",
    first_of_each."UsesOriginal",
    row_number() OVER (PARTITION BY first_of_each."WantedItemId" ORDER BY first_of_each."Rank", first_of_each."FirstLanguage")::integer AS "Position"
FROM first_of_each;

-- A book is wanted once per first language of its chains.
CREATE OR REPLACE VIEW "WantedBookLanguages" AS
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
        chain."WantedItemId",
        chain."FirstLanguage"::text AS "Language",
        chain."Position"
    FROM
        books
        JOIN "WantedItemChains" AS chain ON chain."WantedItemId" = books."WantedItemId"
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
