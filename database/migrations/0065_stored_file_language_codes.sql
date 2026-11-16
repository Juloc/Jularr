-- The language codes of a file's audio and (not forced) subtitle tracks, kept on the file: a title card compares them with the profile's languages instead of
-- reading every track of every episode again. The codes are the tracks' languages before their region ("de-AT" is "de", "pt_BR" is "pt"), lower case.
ALTER TABLE "StoredFiles"
    ADD COLUMN IF NOT EXISTS "AudioLanguageCodes" text[] NOT NULL DEFAULT '{}',
    ADD COLUMN IF NOT EXISTS "SubtitleLanguageCodes" text[] NOT NULL DEFAULT '{}';

CREATE OR REPLACE FUNCTION "StoredFileLanguageCodes"(file_ids bigint[]) RETURNS void LANGUAGE sql AS $$
    UPDATE "StoredFiles" AS file
    SET
        "AudioLanguageCodes" = codes."Audio",
        "SubtitleLanguageCodes" = codes."Subtitle"
    FROM (
        SELECT
            target."Id",
            coalesce(array_agg(DISTINCT lower(split_part(replace(track."Language", '_', '-'), '-', 1))) FILTER (WHERE track."MediaTrackTypeId" = 2), '{}') AS "Audio",
            coalesce(array_agg(DISTINCT lower(split_part(replace(track."Language", '_', '-'), '-', 1))) FILTER (WHERE track."MediaTrackTypeId" = 3 AND NOT track."IsForced"), '{}') AS "Subtitle"
        FROM
            unnest(file_ids) AS target("Id")
            LEFT JOIN "MediaTracks" AS track ON track."StoredFileId" = target."Id" AND track."Language" IS NOT NULL
        GROUP BY
            target."Id"
    ) AS codes
    WHERE
        file."Id" = codes."Id"
        AND (file."AudioLanguageCodes", file."SubtitleLanguageCodes") IS DISTINCT FROM (codes."Audio", codes."Subtitle");
$$;

-- Inserted and deleted tracks both arrive as "changed".
CREATE OR REPLACE FUNCTION "RefreshStoredFileLanguageCodes"() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM "StoredFileLanguageCodes"(ARRAY(SELECT DISTINCT changed."StoredFileId" FROM changed));
    RETURN NULL;
END;
$$;

CREATE OR REPLACE FUNCTION "RefreshStoredFileLanguageCodesOfBoth"() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM "StoredFileLanguageCodes"(ARRAY(SELECT changed."StoredFileId" FROM changed UNION SELECT previous."StoredFileId" FROM previous));
    RETURN NULL;
END;
$$;

-- One statement refreshes each file it touched once, whatever number of tracks it wrote.
DROP TRIGGER IF EXISTS "TR_MediaTracks_LanguageCodes_Insert" ON "MediaTracks";
DROP TRIGGER IF EXISTS "TR_MediaTracks_LanguageCodes_Update" ON "MediaTracks";
DROP TRIGGER IF EXISTS "TR_MediaTracks_LanguageCodes_Delete" ON "MediaTracks";
CREATE TRIGGER "TR_MediaTracks_LanguageCodes_Insert" AFTER INSERT ON "MediaTracks" REFERENCING NEW TABLE AS changed
    FOR EACH STATEMENT EXECUTE FUNCTION "RefreshStoredFileLanguageCodes"();
CREATE TRIGGER "TR_MediaTracks_LanguageCodes_Update" AFTER UPDATE ON "MediaTracks" REFERENCING NEW TABLE AS changed OLD TABLE AS previous
    FOR EACH STATEMENT EXECUTE FUNCTION "RefreshStoredFileLanguageCodesOfBoth"();
CREATE TRIGGER "TR_MediaTracks_LanguageCodes_Delete" AFTER DELETE ON "MediaTracks" REFERENCING OLD TABLE AS changed
    FOR EACH STATEMENT EXECUTE FUNCTION "RefreshStoredFileLanguageCodes"();

-- The files there are now.
SELECT "StoredFileLanguageCodes"(ARRAY(SELECT DISTINCT track."StoredFileId" FROM "MediaTracks" AS track));
