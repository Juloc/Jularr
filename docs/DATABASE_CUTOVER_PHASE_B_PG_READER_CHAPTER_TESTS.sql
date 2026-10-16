\set ON_ERROR_STOP on
\i /tmp/phase_b_reader_content_prepared.sql
\i /tmp/phase_b_reader_nodes_prepared.sql
\i /tmp/phase_b_reader_source_prepared.sql
\i /tmp/phase_b_reader_pages_prepared.sql
\i /tmp/phase_b_reader_bookmark_create_prepared.sql
\i /tmp/phase_b_reader_highlight_create_prepared.sql
\i /tmp/phase_b_reader_bookmark_prepared.sql
\i /tmp/phase_b_reader_highlight_prepared.sql
\i /tmp/phase_b_progress_start_prepared.sql
\i /tmp/phase_b_progress_checkpoint_prepared.sql
BEGIN;
SET LOCAL statement_timeout='20s';
SELECT
    content."WorkId" AS work_id,
    work."PublicId" AS work_public,
    content."WorkEditionId" AS edition_id,
    edition."PublicId" AS edition_public,
    content."BuilderOperationId" AS operation_id
FROM
    "ReaderContent" AS content
INNER JOIN
    "WorkEditions" AS edition ON edition."Id"=content."WorkEditionId"
INNER JOIN
    "Works" AS work ON work."Id"=content."WorkId"
WHERE
    edition."Name"='Reader content fixture' AND content."VariantKey"='original' \gset
SELECT "Id" AS actor_id FROM "Accounts" WHERE "Email"='reader-content-a@example.invalid' \gset
SELECT "Id" AS profile_id FROM "Profiles" WHERE "DisplayName"='Reader content profile' \gset
INSERT INTO "WorkChapters" ("WorkId", "OrderIndex", "DisplayName")
VALUES (:work_id,0,'Chapter A') RETURNING "Id" AS chapter_a, "PublicId" AS chapter_a_public \gset
INSERT INTO "WorkChapters" ("WorkId", "OrderIndex", "DisplayName")
VALUES (:work_id,1,'Chapter B') RETURNING "Id" AS chapter_b, "PublicId" AS chapter_b_public \gset
INSERT INTO "ReaderContent" ("WorkId", "WorkEditionId", "WorkChapterId", "ContentRevision", "StorageKey", "ReaderLayoutTypeId",
    "ContentHash", "ParserVersion", "BuilderOperationId", "NodeCount", "PageCount")
VALUES (:work_id,:edition_id,:chapter_a,1,'reader-chapter-a',1,decode(repeat('b1',32),'hex'),'fixture',:operation_id,1,0)
RETURNING "Id" AS content_a \gset
INSERT INTO "ReaderContent" ("WorkId", "WorkEditionId", "WorkChapterId", "ContentRevision", "StorageKey", "ReaderLayoutTypeId",
    "ContentHash", "ParserVersion", "BuilderOperationId", "NodeCount", "PageCount")
VALUES (:work_id,:edition_id,:chapter_b,1,'reader-chapter-b',1,decode(repeat('b2',32),'hex'),'fixture',:operation_id,1,0)
RETURNING "Id" AS content_b \gset
INSERT INTO "ReaderContentNodes" ("ReaderContentId", "StableId", "OrderIndex", "ReaderNodeTypeId", "ReaderTextBlockTypeId", "CharacterCount")
VALUES (:content_a,'chapter-a',0,1,1,100),(:content_b,'chapter-b',0,1,1,100);
UPDATE "ReaderContent" SET "PublishedAt"=now() WHERE "Id" IN (:content_a,:content_b);
SET CONSTRAINTS ALL IMMEDIATE;
EXECUTE phase_b_reader_content(:actor_id,:profile_id,:'edition_public',NULL,'original',1);
SELECT 1 / CASE WHEN :ROW_COUNT=1 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_content(:actor_id,:profile_id,:'edition_public',:'chapter_a_public','original',1) \gset
SELECT 1 / CASE WHEN :'WorkChapterId'::uuid=:'chapter_a_public'::uuid AND :NodeCount=1 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_nodes(:actor_id,:profile_id,:'edition_public',:'chapter_b_public','original',1,0,25) \gset
SELECT 1 / CASE WHEN :'StableId'='chapter-b' THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_source(:actor_id,:profile_id,:'edition_public',:'chapter_a_public','original',1) \gset
SELECT 1 / CASE WHEN :'StorageKey'='reader-chapter-a' THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_content(:actor_id,:profile_id,:'edition_public',gen_random_uuid(),'original',1);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_bookmark_create(:actor_id,:profile_id,:'edition_public',:'chapter_b_public','original',1,
    gen_random_uuid(),'{"blockId":"chapter-b","characterOffset":5}','B',now()) \gset
EXECUTE phase_b_reader_bookmark(:actor_id,:profile_id,:'PublicId') \gset
SELECT 1 / CASE WHEN :'WorkChapterId'::uuid=:'chapter_b_public'::uuid THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_highlight_create(:actor_id,:profile_id,:'edition_public',:'chapter_a_public','original',1,
    gen_random_uuid(),'{"blockId":"chapter-a","characterOffset":1}','{"blockId":"chapter-a","characterOffset":3}','ab',NULL,now()) \gset
EXECUTE phase_b_reader_highlight(:actor_id,:profile_id,:'PublicId') \gset
SELECT 1 / CASE WHEN :'WorkChapterId'::uuid=:'chapter_a_public'::uuid THEN 1 ELSE 0 END;

INSERT INTO "ReaderContent" ("WorkId", "WorkEditionId", "WorkChapterId", "ContentRevision", "StorageKey", "VariantKey", "ReaderLayoutTypeId",
    "ContentHash", "ParserVersion", "BuilderOperationId", "NodeCount", "PageCount")
VALUES (:work_id,:edition_id,:chapter_a,1,'reader-chapter-a-images','images',2,decode(repeat('b3',32),'hex'),'fixture',:operation_id,0,1)
RETURNING "Id" AS image_a \gset
INSERT INTO "ReaderContent" ("WorkId", "WorkEditionId", "WorkChapterId", "ContentRevision", "StorageKey", "VariantKey", "ReaderLayoutTypeId",
    "ContentHash", "ParserVersion", "BuilderOperationId", "NodeCount", "PageCount")
VALUES (:work_id,:edition_id,:chapter_b,1,'reader-chapter-b-images','images',2,decode(repeat('b4',32),'hex'),'fixture',:operation_id,0,1)
RETURNING "Id" AS image_b \gset
INSERT INTO "ReaderPages" ("ReaderContentId", "PageIndex", "TextAnchor") VALUES (:image_a,0,'a'),(:image_b,0,'b');
UPDATE "ReaderContent" SET "PublishedAt"=now() WHERE "Id" IN (:image_a,:image_b);
EXECUTE phase_b_reader_pages(:actor_id,:profile_id,:'edition_public',:'chapter_b_public',1,'images',0,25) \gset
SELECT 1 / CASE WHEN :'TextAnchor'='b' AND :'WorkChapterId'::uuid=:'chapter_b_public'::uuid THEN 1 ELSE 0 END;

EXECUTE phase_b_progress_start(:actor_id,:profile_id,:'work_public',NULL,:'chapter_a_public',NULL,NULL,2,now()) \gset
SELECT "Id" AS progress_id FROM "MediaProgress" WHERE "PublicId"=:'MediaProgressId' \gset
EXECUTE phase_b_progress_checkpoint(:actor_id,:profile_id,:'MediaProgressId',1,gen_random_uuid(),decode(repeat('b5',32),'hex'),1,
    now(),NULL,NULL,FALSE,NULL,NULL,:'edition_public',1,'original','{"blockId":"chapter-b","characterOffset":5}',100,NULL,NULL);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_checkpoint(:actor_id,:profile_id,:'MediaProgressId',1,gen_random_uuid(),decode(repeat('b6',32),'hex'),1,
    now(),NULL,NULL,FALSE,NULL,NULL,:'edition_public',1,'original','{"blockId":"chapter-a","characterOffset":5}',100,NULL,NULL) \gset
SELECT 1 / CASE WHEN :Revision=2 AND (SELECT "ReaderContentId"=:content_a
    FROM "ReadingProgressPositions" WHERE "MediaProgressId"=:progress_id) THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_start(:actor_id,:profile_id,:'work_public',NULL,NULL,NULL,:'edition_public',2,now()) \gset
EXECUTE phase_b_progress_checkpoint(:actor_id,:profile_id,:'MediaProgressId',1,gen_random_uuid(),decode(repeat('b7',32),'hex'),1,
    now(),NULL,NULL,FALSE,NULL,NULL,:'edition_public',1,'original','{"blockId":"chapter-a","characterOffset":5}',100,NULL,NULL);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;

DO $exact_chapter$
DECLARE
    progress_id bigint;
    content_b bigint;
    content_a bigint;
    whole_progress bigint;
    original_content record;
    rejected text;
BEGIN
    SELECT
        progress."Id"
    INTO progress_id
    FROM
        "MediaProgress" AS progress
    INNER JOIN
        "WorkChapters" AS chapter ON chapter."Id"=progress."WorkChapterId"
    INNER JOIN
        "Works" AS work ON work."Id"=progress."WorkId"
    WHERE
        work."CanonicalTitle"='Reader content publication fixture' AND chapter."DisplayName"='Chapter A';
    SELECT
        content."Id"
    INTO content_b
    FROM
        "ReaderContent" AS content
    INNER JOIN
        "WorkChapters" AS chapter ON chapter."Id"=content."WorkChapterId"
    WHERE
        chapter."DisplayName"='Chapter B' AND content."VariantKey"='original';
    BEGIN
        UPDATE "ReadingProgressPositions" SET "ReaderContentId"=content_b,"Locator"='{"blockId":"chapter-b","characterOffset":5}'
        WHERE "MediaProgressId"=progress_id;
        SET CONSTRAINTS ALL IMMEDIATE;
        RAISE EXCEPTION 'Chapter progress accepted another chapter payload';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'CK_ReadingProgressPositions_ExactTarget' THEN RAISE; END IF;
    END;
    SELECT
        content."Id"
    INTO content_a
    FROM
        "ReaderContent" AS content
    INNER JOIN
        "WorkChapters" AS chapter ON chapter."Id"=content."WorkChapterId"
    WHERE
        chapter."DisplayName"='Chapter A' AND content."VariantKey"='original';
    SELECT
        progress."Id"
    INTO whole_progress
    FROM
        "MediaProgress" AS progress
    INNER JOIN
        "Works" AS work ON work."Id"=progress."WorkId"
    WHERE
        work."CanonicalTitle"='Reader content publication fixture' AND progress."WorkEditionId" IS NOT NULL;
    BEGIN
        UPDATE "ReadingProgressPositions" SET "ReaderContentId"=content_a,"Locator"='{"blockId":"chapter-a","characterOffset":5}'
        WHERE "MediaProgressId"=whole_progress;
        SET CONSTRAINTS ALL IMMEDIATE;
        RAISE EXCEPTION 'Whole-edition progress accepted a chapter-scoped payload';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'CK_ReadingProgressPositions_ExactTarget' THEN RAISE; END IF;
    END;
    SELECT
        content."WorkId",
        content."WorkEditionId",
        content."BuilderOperationId"
    INTO original_content
    FROM
        "ReaderContent" AS content
    INNER JOIN
        "WorkEditions" AS edition ON edition."Id"=content."WorkEditionId"
    WHERE
        edition."Name"='Reader content fixture' AND content."VariantKey"='original' AND content."WorkChapterId" IS NULL;
    BEGIN
        INSERT INTO "ReaderContent" ("WorkId", "WorkEditionId", "ContentRevision", "StorageKey", "ReaderLayoutTypeId",
            "ContentHash", "ParserVersion", "BuilderOperationId", "NodeCount", "PageCount")
        VALUES (original_content."WorkId",original_content."WorkEditionId",1,'reader-duplicate-whole',1,
            decode(repeat('ff',32),'hex'),'fixture',original_content."BuilderOperationId",1,0);
        RAISE EXCEPTION 'NULL chapter allowed a second whole-edition revision';
    EXCEPTION WHEN unique_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'UX_ReaderContent_Edition_Chapter_Variant_Revision' THEN RAISE; END IF;
    END;
END $exact_chapter$;
ROLLBACK;
