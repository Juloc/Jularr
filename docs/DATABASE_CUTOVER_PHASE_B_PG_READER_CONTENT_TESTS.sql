\set ON_ERROR_STOP on
SET statement_timeout = '20s';
\i /tmp/phase_b_reader_content_prepared.sql
\i /tmp/phase_b_reader_nodes_prepared.sql
\i /tmp/phase_b_reader_source_prepared.sql
\i /tmp/phase_b_reader_publish_prepared.sql
\i /tmp/phase_b_reader_bookmark_create_prepared.sql
\i /tmp/phase_b_reader_highlight_create_prepared.sql
\i /tmp/phase_b_reader_bookmark_edit_prepared.sql
\i /tmp/phase_b_reader_highlight_edit_prepared.sql
\i /tmp/phase_b_reader_bookmark_delete_prepared.sql
\i /tmp/phase_b_reader_highlight_delete_prepared.sql
\i /tmp/phase_b_reader_bookmark_prepared.sql
\i /tmp/phase_b_reader_highlight_prepared.sql
BEGIN;
INSERT INTO "UiLocales" ("Locale", "Name") VALUES ('reader-content-test', 'Reader content') RETURNING "Id" AS locale_id \gset
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('reader-content-a@example.invalid', 'Reader content A', 1) RETURNING "Id" AS actor_id \gset
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('reader-content-b@example.invalid', 'Reader content B', 2) RETURNING "Id" AS other_id \gset
INSERT INTO "Profiles" ("OwnerAccountId", "DisplayName", "UiLocaleId")
VALUES (:actor_id, 'Reader content profile', :locale_id) RETURNING "Id" AS profile_id \gset
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (:actor_id, :profile_id);
INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle")
VALUES (3, 'Reader content publication fixture') RETURNING "Id" AS work_id, "PublicId" AS work_public \gset
INSERT INTO "WorkEditions" ("WorkId", "Name")
VALUES (:work_id, 'Reader content fixture') RETURNING "Id" AS edition_id, "PublicId" AS edition_public \gset
INSERT INTO "Operations" ("OperationKindKey", "OperationStatusTypeId", "ClaimedBy", "ClaimedUntil")
VALUES ('reader_parse', 2, 'reader-content-builder', now()+INTERVAL '1 hour') RETURNING "Id" AS operation_id \gset
INSERT INTO "ReaderContent" ("WorkId", "WorkEditionId", "ContentRevision", "StorageKey", "ReaderLayoutTypeId",
    "ContentHash", "ParserVersion", "BuilderOperationId", "NodeCount", "PageCount")
VALUES (:work_id, :edition_id, 1, 'reader-content-original', 1, decode(repeat('a1',32),'hex'), 'fixture-v1', :operation_id, 1000, 0)
RETURNING "Id" AS content_id \gset
EXECUTE phase_b_reader_content(:actor_id,:profile_id,:'edition_public',NULL,'original',1);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_source(:actor_id,:profile_id,:'edition_public',NULL,'original',1);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_nodes(:actor_id,:profile_id,:'edition_public',NULL,'original',1,0,25);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
INSERT INTO "ReaderContentNodes" ("ReaderContentId", "StableId", "OrderIndex", "ReaderNodeTypeId", "ReaderTextBlockTypeId", "CharacterCount")
SELECT :content_id, 'paragraph-'||i, i, 1, 1, 100 FROM generate_series(0,999) AS i;
SAVEPOINT builder_gates;
UPDATE "Operations" SET "ClaimedUntil"=now()-INTERVAL '1 second' WHERE "Id"=:operation_id;
EXECUTE phase_b_reader_publish(:content_id,decode(repeat('a1',32),'hex'),now(),'reader-content-builder');
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
ROLLBACK TO SAVEPOINT builder_gates;
UPDATE "Operations" SET "OperationStatusTypeId"=5,"ClaimedBy"=NULL,"ClaimedUntil"=NULL WHERE "Id"=:operation_id;
EXECUTE phase_b_reader_publish(:content_id,decode(repeat('a1',32),'hex'),now(),'reader-content-builder');
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
ROLLBACK TO SAVEPOINT builder_gates;
UPDATE "Operations" SET "OperationKindKey"='not-reader' WHERE "Id"=:operation_id;
EXECUTE phase_b_reader_publish(:content_id,decode(repeat('a1',32),'hex'),now(),'reader-content-builder');
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
ROLLBACK TO SAVEPOINT builder_gates;
RELEASE SAVEPOINT builder_gates;
EXECUTE phase_b_reader_publish(:content_id,decode(repeat('a1',32),'hex'),now(),'wrong-worker');
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_publish(:content_id,decode(repeat('ff',32),'hex'),now(),'reader-content-builder');
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_publish(:content_id,decode(repeat('a1',32),'hex'),now(),'reader-content-builder');
SELECT 1 / CASE WHEN :ROW_COUNT=1 THEN 1 ELSE 0 END;
SET CONSTRAINTS ALL IMMEDIATE;
EXECUTE phase_b_reader_publish(:content_id,decode(repeat('a1',32),'hex'),now(),'reader-content-builder');
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_content(:actor_id,:profile_id,:'edition_public',NULL,'original',1) \gset
SELECT 1 / CASE WHEN :ReaderLayoutTypeId=1 AND :NodeCount=1000 AND :PageCount=0
    AND :'LanguageTag'='und' AND :'VariantKey'='original' THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_source(:actor_id,:profile_id,:'edition_public',NULL,'original',1) \gset
SELECT 1 / CASE WHEN :'StorageKey'='reader-content-original' THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_nodes(:actor_id,:profile_id,:'edition_public',NULL,'original',1,950,1) \gset
SELECT 1 / CASE WHEN :OrderIndex=950 AND :'StableId'='paragraph-950' THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_nodes(:actor_id,:profile_id,:'edition_public',NULL,'original',1,950,25);
SELECT 1 / CASE WHEN :ROW_COUNT=25 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_nodes(:actor_id,:profile_id,:'edition_public',NULL,'original',1,990,100);
SELECT 1 / CASE WHEN :ROW_COUNT=10 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_nodes(:actor_id,:profile_id,:'edition_public',NULL,'original',1,-1,25);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_nodes(:actor_id,:profile_id,:'edition_public',NULL,'original',1,0,101);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_content(:actor_id,:profile_id,:'edition_public',NULL,'original''; DROP TABLE "Accounts";--',1);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_content(:actor_id,:profile_id,:'edition_public',NULL,'original',2);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
ANALYZE "ReaderContent";
ANALYZE "ReaderContentNodes";
EXPLAIN (ANALYZE, BUFFERS) EXECUTE phase_b_reader_nodes(:actor_id,:profile_id,:'edition_public',NULL,'original',1,950,25);

INSERT INTO "Operations" ("OperationKindKey", "OperationStatusTypeId", "ClaimedBy", "ClaimedUntil")
VALUES ('reader_translate', 2, 'reader-content-builder', now()+INTERVAL '1 hour') RETURNING "Id" AS translation_operation \gset
INSERT INTO "ReaderContent" ("WorkId", "WorkEditionId", "ContentRevision", "StorageKey", "ReaderLayoutTypeId",
    "ReaderVariantTypeId", "VariantKey", "LanguageTag", "SourceReaderContentId", "ContentHash", "ParserVersion", "BuilderOperationId", "NodeCount", "PageCount")
VALUES (:work_id,:edition_id,1,'reader-content-translation',1,3,'de','de',:content_id,decode(repeat('a2',32),'hex'),'fixture-v1',:translation_operation,1,0)
RETURNING "Id" AS translation_id \gset
INSERT INTO "ReaderContentNodes" ("ReaderContentId", "StableId", "OrderIndex", "ReaderNodeTypeId", "ReaderTextBlockTypeId", "CharacterCount", "SourceReaderContentId", "SourceNodeId")
SELECT :translation_id,'translated-0',0,1,1,120,:content_id,node."Id"
FROM "ReaderContentNodes" AS node WHERE node."ReaderContentId"=:content_id AND node."OrderIndex"=0;
EXECUTE phase_b_reader_publish(:translation_id,decode(repeat('a2',32),'hex'),now(),'reader-content-builder');
SELECT 1 / CASE WHEN :ROW_COUNT=1 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_content(:actor_id,:profile_id,:'edition_public',NULL,'de',1) \gset
SELECT 1 / CASE WHEN :ReaderVariantTypeId=3 AND :'LanguageTag'='de' AND :NodeCount=1 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_nodes(:actor_id,:profile_id,:'edition_public',NULL,'de',1,0,1) \gset
SELECT 1 / CASE WHEN :'SourceStableId'='paragraph-0' AND :'StableId'='translated-0' THEN 1 ELSE 0 END;

INSERT INTO "ReaderContent" ("WorkId", "WorkEditionId", "ContentRevision", "StorageKey", "ReaderLayoutTypeId",
    "VariantKey", "ContentHash", "ParserVersion", "BuilderOperationId", "NodeCount", "PageCount")
VALUES (:work_id,:edition_id,1,'reader-content-images',2,'images',decode(repeat('a3',32),'hex'),'fixture-v1',:operation_id,0,1000)
RETURNING "Id" AS image_id \gset
INSERT INTO "ReaderPages" ("ReaderContentId", "PageIndex") SELECT :image_id, i FROM generate_series(0,999) AS i;
EXECUTE phase_b_reader_publish(:image_id,decode(repeat('a3',32),'hex'),now(),'reader-content-builder');
SELECT 1 / CASE WHEN :ROW_COUNT=1 THEN 1 ELSE 0 END;
INSERT INTO "ReaderContent" ("WorkId", "WorkEditionId", "ContentRevision", "StorageKey", "ReaderLayoutTypeId",
    "VariantKey", "ContentHash", "ParserVersion", "BuilderOperationId", "NodeCount", "PageCount", "SourcePageCount", "HasTextLayer")
VALUES (:work_id,:edition_id,1,'reader-content-fixed',3,'fixed',decode(repeat('a4',32),'hex'),'fixture-v1',:operation_id,0,2,20,TRUE)
RETURNING "Id" AS fixed_id \gset
INSERT INTO "ReaderPages" ("ReaderContentId", "PageIndex", "SourcePageIndex") VALUES (:fixed_id,0,5),(:fixed_id,1,7);
EXECUTE phase_b_reader_publish(:fixed_id,decode(repeat('a4',32),'hex'),now(),'reader-content-builder');
SELECT 1 / CASE WHEN :ROW_COUNT=1 THEN 1 ELSE 0 END;

SELECT 1 / CASE WHEN "IsReaderLocatorValid"(:content_id,'{"blockId":"paragraph-0","characterOffset":100,"fallbackPercent":100}')
    AND "IsReaderLocatorValid"(:image_id,'{"pageIndex":999,"viewport":{"x":1,"y":0,"zoom":2}}')
    AND "IsReaderLocatorValid"(:fixed_id,'{"logicalPageIndex":0,"sourcePageIndex":5,"sourceRegion":{"x":0.1,"y":0.2,"width":0.3,"height":0.4}}')
    THEN 1 ELSE 0 END;
SELECT 1 / CASE WHEN bool_and(NOT "IsReaderLocatorValid"(invalid.content_id, invalid.locator)) THEN 1 ELSE 0 END
FROM (VALUES
    (:content_id,'{"blockId":"missing","characterOffset":0}'::jsonb),
    (:content_id,'{"blockId":"paragraph-0","characterOffset":101}'),
    (:content_id,'{"blockId":"paragraph-0","characterOffset":-1}'),
    (:content_id,'{"blockId":"paragraph-0","characterOffset":2147483648}'),
    (:content_id,'{"blockId":"paragraph-0","characterOffset":"1"}'),
    (:content_id,'{"blockId":"paragraph-0","characterOffset":null}'),
    (:content_id,'{"blockId":"paragraph-0","characterOffset":1,"fallbackPercent":100.1}'),
    (:content_id,'{"blockId":"paragraph-0","characterOffset":1,"viewport":{"x":0,"y":0,"zoom":1}}'),
    (:image_id,'{"pageIndex":1000}'),
    (:image_id,'{"pageIndex":0,"unknown":true}'),
    (:image_id,'{"pageIndex":0,"viewport":{"x":0,"y":0,"zoom":0}}'),
    (:image_id,'{"pageIndex":0,"viewport":{"x":0,"y":0,"zoom":"NaN"}}'),
    (:fixed_id,'{"logicalPageIndex":0,"sourcePageIndex":7}'),
    (:fixed_id,'{"logicalPageIndex":2,"sourcePageIndex":5}'),
    (:fixed_id,'{"logicalPageIndex":0,"sourcePageIndex":5,"sourceRegion":{"x":0.9,"y":0,"width":0.2,"height":1}}')
) AS invalid(content_id,locator);

SELECT gen_random_uuid() AS bookmark_public, gen_random_uuid() AS highlight_public \gset
EXECUTE phase_b_reader_bookmark_create(:other_id,:profile_id,:'edition_public',NULL,'original',1,:'bookmark_public','{"blockId":"paragraph-0","characterOffset":1}',NULL,now());
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_bookmark_create(:actor_id,:profile_id,:'edition_public',NULL,'original',1,:'bookmark_public','{"blockId":"paragraph-0","characterOffset":1}','quote''; --',now()) \gset
SELECT 1 / CASE WHEN :Revision=1 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_bookmark_create(:actor_id,:profile_id,:'edition_public',NULL,'original',1,:'bookmark_public','{"blockId":"paragraph-0","characterOffset":2}','replacement',now());
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_bookmark(:actor_id,:profile_id,:'bookmark_public') \gset
SELECT 1 / CASE WHEN :Revision=1 AND :'Label'='quote''; --' AND :'Locator'::jsonb->>'characterOffset'='1' THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_bookmark(:other_id,:profile_id,:'bookmark_public');
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_bookmark_edit(:actor_id,:profile_id,:'bookmark_public',1,'{"blockId":"paragraph-0","characterOffset":2}','second',now()) \gset
SELECT 1 / CASE WHEN :Revision=2 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_bookmark_edit(:actor_id,:profile_id,:'bookmark_public',1,'{"blockId":"paragraph-0","characterOffset":3}','stale',now());
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_bookmark_delete(:actor_id,:profile_id,:'bookmark_public',1);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_highlight_create(:actor_id,:profile_id,:'edition_public',NULL,'original',1,:'highlight_public',
    '{"blockId":"paragraph-0","characterOffset":10}','{"blockId":"paragraph-0","characterOffset":15}','abcde','quote''; --',now()) \gset
SELECT 1 / CASE WHEN :Revision=1 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_highlight_edit(:actor_id,:profile_id,:'highlight_public',1,
    '{"blockId":"paragraph-0","characterOffset":10}','{"blockId":"paragraph-0","characterOffset":15}','abcde','edited',now()) \gset
SELECT 1 / CASE WHEN :Revision=2 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_highlight_edit(:other_id,:profile_id,:'highlight_public',2,
    '{"blockId":"paragraph-0","characterOffset":10}','{"blockId":"paragraph-0","characterOffset":15}','abcde','forbidden',now());
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (:other_id,:profile_id);
EXECUTE phase_b_reader_content(:other_id,:profile_id,:'edition_public',NULL,'original',1);
SELECT 1 / CASE WHEN :ROW_COUNT=1 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_highlight_edit(:other_id,:profile_id,:'highlight_public',2,
    '{"blockId":"paragraph-0","characterOffset":10}','{"blockId":"paragraph-0","characterOffset":15}','abcde','shared',now()) \gset
SELECT 1 / CASE WHEN :Revision=3 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_highlight(:other_id,:profile_id,:'highlight_public') \gset
SELECT 1 / CASE WHEN :Revision=3 AND :'SelectedText'='abcde' AND :'Note'='shared' THEN 1 ELSE 0 END;
DELETE FROM "AccountProfiles" WHERE "AccountId"=:other_id AND "ProfileId"=:profile_id;
EXECUTE phase_b_reader_source(:other_id,:profile_id,:'edition_public',NULL,'original',1);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_nodes(:other_id,:profile_id,:'edition_public',NULL,'original',1,0,25);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_highlight_delete(:other_id,:profile_id,:'highlight_public',3);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_highlight(:other_id,:profile_id,:'highlight_public');
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
SAVEPOINT annotation_rollback;
EXECUTE phase_b_reader_bookmark_delete(:actor_id,:profile_id,:'bookmark_public',2);
SELECT 1 / CASE WHEN :ROW_COUNT=1 THEN 1 ELSE 0 END;
ROLLBACK TO SAVEPOINT annotation_rollback;
SELECT 1 / CASE WHEN EXISTS (SELECT 1 FROM "ReaderBookmarks" WHERE "PublicId"=:'bookmark_public' AND "Revision"=2) THEN 1 ELSE 0 END;
UPDATE "Accounts" SET "IsEnabled"=FALSE WHERE "Id"=:actor_id;
EXECUTE phase_b_reader_content(:actor_id,:profile_id,:'edition_public',NULL,'original',1);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_bookmark_delete(:actor_id,:profile_id,:'bookmark_public',2);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
UPDATE "Accounts" SET "IsEnabled"=TRUE WHERE "Id"=:actor_id;
COMMIT;

DO $reader_invariants$
DECLARE
    content_id bigint;
    edition_id bigint;
    work_id bigint;
    builder_id bigint;
    profile_id bigint;
    image_id bigint;
    draft_id bigint;
    other_edition bigint;
    version_id bigint;
    other_version bigint;
    asset_id bigint;
    root_id bigint;
    file_id bigint;
    rejected text;
BEGIN
    SELECT content."Id", content."WorkEditionId", content."WorkId", content."BuilderOperationId"
    INTO content_id,edition_id,work_id,builder_id
    FROM "ReaderContent" AS content INNER JOIN "WorkEditions" AS edition ON edition."Id"=content."WorkEditionId"
    WHERE edition."Name"='Reader content fixture' AND content."VariantKey"='original';
    SELECT "Id" INTO profile_id FROM "Profiles" WHERE "DisplayName"='Reader content profile';
    SELECT "Id" INTO image_id FROM "ReaderContent" WHERE "WorkEditionId"=edition_id AND "VariantKey"='images';
    BEGIN
        UPDATE "ReaderContentNodes" SET "CharacterCount"=101 WHERE "ReaderContentId"=content_id AND "OrderIndex"=0;
        RAISE EXCEPTION 'Published node mutated';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'CK_ReaderContent_ImmutableIndex' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO "ReaderContentNodes" ("ReaderContentId", "StableId", "OrderIndex", "ReaderNodeTypeId") VALUES (content_id,'late',1000,3);
        RAISE EXCEPTION 'Late published node inserted';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'CK_ReaderContent_ImmutableIndex' THEN RAISE; END IF;
    END;
    BEGIN
        DELETE FROM "ReaderPages" WHERE "ReaderContentId"=image_id AND "PageIndex"=0;
        SET CONSTRAINTS ALL IMMEDIATE;
        RAISE EXCEPTION 'Published page deleted without its content';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'CK_ReaderContent_PublishedIndex' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO "ReaderHighlights" ("ProfileId", "ReaderContentId", "StartLocator", "EndLocator", "SelectedText")
        VALUES (profile_id,content_id,'{"blockId":"paragraph-0","characterOffset":12}','{"blockId":"paragraph-0","characterOffset":17}','abcde');
        RAISE EXCEPTION 'Overlapping highlight accepted';
    EXCEPTION WHEN exclusion_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'UX_ReaderHighlights_NoOverlap' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO "ReaderHighlights" ("ProfileId", "ReaderContentId", "StartLocator", "EndLocator", "SelectedText")
        VALUES (profile_id,image_id,'{"pageIndex":0}','{"pageIndex":1}','a');
        RAISE EXCEPTION 'Image highlight accepted';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'CK_ReaderHighlights_Anchors' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO "ReaderHighlights" ("ProfileId", "ReaderContentId", "StartLocator", "EndLocator", "SelectedText")
        VALUES (profile_id,content_id,'{"blockId":"paragraph-2","characterOffset":10}','{"blockId":"paragraph-1","characterOffset":15}','abcde');
        RAISE EXCEPTION 'Reversed highlight accepted';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'CK_ReaderHighlights_Range' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO "ReaderHighlights" ("ProfileId", "ReaderContentId", "StartLocator", "EndLocator", "SelectedText", "Note")
        VALUES (profile_id,content_id,'{"blockId":"paragraph-3","characterOffset":0}','{"blockId":"paragraph-3","characterOffset":10}','abcdefghij',repeat('x',2001));
        RAISE EXCEPTION 'Oversized highlight note accepted';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'CK_ReaderHighlights_Note' THEN RAISE; END IF;
    END;
    INSERT INTO "WorkEditions" ("WorkId", "Name") VALUES (work_id,'Reader source other edition') RETURNING "Id" INTO other_edition;
    BEGIN
        INSERT INTO "ReaderContent" ("WorkId", "WorkEditionId", "ContentRevision", "StorageKey", "ReaderLayoutTypeId", "ReaderVariantTypeId", "SourceReaderContentId", "ContentHash", "ParserVersion", "BuilderOperationId", "NodeCount", "PageCount")
        VALUES (work_id,other_edition,1,'reader-source-wrong-edition',1,3,content_id,decode(repeat('ff',32),'hex'),'fixture-v1',builder_id,1,0);
        RAISE EXCEPTION 'Derived content changed source edition';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'CK_ReaderContent_SourceTarget' THEN RAISE; END IF;
    END;
    INSERT INTO "WorkVersions" ("WorkId", "WorkEditionId") VALUES (work_id,edition_id) RETURNING "Id" INTO version_id;
    INSERT INTO "WorkVersions" ("WorkId", "WorkEditionId") VALUES (work_id,other_edition) RETURNING "Id" INTO other_version;
    INSERT INTO "MediaAssets" ("WorkId", "WorkVersionId", "MediaAssetTypeId") VALUES (work_id,version_id,3) RETURNING "Id" INTO asset_id;
    INSERT INTO "LibraryRoots" ("DisplayName", "RootPath") VALUES ('Reader fixture root','/fixture-only-not-mounted/reader') RETURNING "Id" INTO root_id;
    INSERT INTO "StoredFiles" ("LibraryRootId", "MediaAssetId", "WorkVersionId", "NormalizedRelativePath")
    VALUES (root_id,asset_id,version_id,'fixture.epub') RETURNING "Id" INTO file_id;
    BEGIN
        INSERT INTO "ReaderContent" ("WorkId", "WorkEditionId", "ContentRevision", "StorageKey", "ReaderLayoutTypeId", "SourceWorkVersionId", "SourceStoredFileId", "ContentHash", "ParserVersion", "BuilderOperationId", "NodeCount", "PageCount")
        VALUES (work_id,edition_id,3,'reader-source-wrong-version',1,other_version,file_id,decode(repeat('ff',32),'hex'),'fixture-v1',builder_id,1,0);
        RAISE EXCEPTION 'Reader file escaped its exact version';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected NOT IN ('FK_ReaderContent_File','FK_ReaderContent_Version') THEN RAISE; END IF;
    END;
    INSERT INTO "ReaderContent" ("WorkId", "WorkEditionId", "ContentRevision", "StorageKey", "ReaderLayoutTypeId", "ContentHash", "ParserVersion", "BuilderOperationId", "NodeCount", "PageCount")
    VALUES (work_id,edition_id,2,'reader-content-incomplete',1,decode(repeat('a5',32),'hex'),'fixture-v1',builder_id,2,0)
    RETURNING "Id" INTO draft_id;
    INSERT INTO "ReaderContentNodes" ("ReaderContentId", "StableId", "OrderIndex", "ReaderNodeTypeId", "ReaderTextBlockTypeId", "CharacterCount")
    VALUES (draft_id,'one',0,1,1,10);
    BEGIN
        UPDATE "ReaderContent" SET "PublishedAt"=now() WHERE "Id"=draft_id;
        SET CONSTRAINTS ALL IMMEDIATE;
        RAISE EXCEPTION 'Incomplete revision published';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'CK_ReaderContent_PublishedIndex' THEN RAISE; END IF;
    END;
    INSERT INTO "ReaderContentNodes" ("ReaderContentId", "StableId", "OrderIndex", "ReaderNodeTypeId", "ReaderTextBlockTypeId", "CharacterCount")
    VALUES (draft_id,'gap',2,1,1,10);
    BEGIN
        UPDATE "ReaderContent" SET "PublishedAt"=now() WHERE "Id"=draft_id;
        SET CONSTRAINTS ALL IMMEDIATE;
        RAISE EXCEPTION 'Gapped revision published';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'CK_ReaderContent_PublishedIndex' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO "ReaderContent" ("WorkId", "WorkEditionId", "ContentRevision", "StorageKey", "ReaderLayoutTypeId", "ReaderVariantTypeId", "VariantKey", "SourceReaderContentId", "ContentHash", "ParserVersion", "BuilderOperationId", "NodeCount", "PageCount")
        VALUES (work_id,edition_id,2,'reader-content-unpublished-source',1,3,'de',draft_id,decode(repeat('a6',32),'hex'),'fixture-v1',builder_id,1,0);
        RAISE EXCEPTION 'Unpublished source accepted';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'CK_ReaderContent_PublishedSource' THEN RAISE; END IF;
    END;
    DELETE FROM "ReaderContentNodes" WHERE "ReaderContentId"=draft_id;
    DELETE FROM "ReaderContent" WHERE "Id"=draft_id;
END $reader_invariants$;

BEGIN;
INSERT INTO "ReaderContent" ("WorkId", "WorkEditionId", "ContentRevision", "StorageKey", "ReaderLayoutTypeId", "VariantKey", "ContentHash", "ParserVersion", "BuilderOperationId", "NodeCount", "PageCount")
VALUES (:work_id,:edition_id,1,'reader-content-race',1,'race',decode(repeat('a7',32),'hex'),'fixture-v1',:operation_id,1,0)
RETURNING "Id" AS race_id \gset
INSERT INTO "ReaderContentNodes" ("ReaderContentId", "StableId", "OrderIndex", "ReaderNodeTypeId", "ReaderTextBlockTypeId", "CharacterCount") VALUES (:race_id,'race-0',0,1,1,10);
COMMIT;
