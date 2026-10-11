\set ON_ERROR_STOP on
BEGIN;
SET LOCAL statement_timeout = '20s';
\i /tmp/phase_b_reader_bookmarks_prepared.sql
\i /tmp/phase_b_reader_highlights_prepared.sql
\i /tmp/phase_b_reader_pages_prepared.sql

INSERT INTO "UiLocales" ("Locale", "Name")
VALUES ('phase-b-reader-queries', 'Phase B reader') RETURNING "Id" AS locale_id \gset
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('reader-actor@example.invalid', 'Reader actor', 1) RETURNING "Id" AS actor_id \gset
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('reader-other@example.invalid', 'Reader other', 2) RETURNING "Id" AS other_id \gset
INSERT INTO "Profiles" ("OwnerAccountId", "DisplayName", "UiLocaleId")
VALUES (:actor_id, 'Reader primary', :locale_id) RETURNING "Id" AS profile_id \gset
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (:actor_id, :profile_id);
INSERT INTO "Profiles" ("OwnerAccountId", "DisplayName", "UiLocaleId")
VALUES (:other_id, 'Reader secondary', :locale_id) RETURNING "Id" AS other_profile_id \gset
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (:other_id, :other_profile_id);

INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle")
VALUES (3, 'Reader query work A') RETURNING "Id" AS work_id, "PublicId" AS work_public_id \gset
INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle")
VALUES (3, 'Reader query work B') RETURNING "Id" AS other_work_id, "PublicId" AS other_work_public_id \gset
INSERT INTO "WorkEditions" ("WorkId", "Name")
VALUES (:work_id, 'Reader query edition A') RETURNING "Id" AS edition_id, "PublicId" AS edition_public_id \gset
INSERT INTO "WorkEditions" ("WorkId", "Name")
VALUES (:other_work_id, 'Reader query edition B') RETURNING "Id" AS other_edition_id \gset
INSERT INTO "Operations" ("OperationKindKey", "OperationStatusTypeId", "ClaimedBy", "ClaimedUntil")
VALUES ('reader_parse', 2, 'reader-fixture', now()+INTERVAL '1 hour') RETURNING "Id" AS operation_id \gset
INSERT INTO "ReaderContent" ("WorkId", "WorkEditionId", "ContentRevision", "StorageKey", "ReaderLayoutTypeId", "ContentHash", "ParserVersion", "BuilderOperationId", "NodeCount", "PageCount")
VALUES (:work_id, :edition_id, 1, 'reader-query-fixture-a', 1, decode(repeat('a1',32),'hex'), 'fixture', :operation_id, 4, 0) RETURNING "Id" AS content_id \gset
INSERT INTO "ReaderContent" ("WorkId", "WorkEditionId", "ContentRevision", "StorageKey", "ReaderLayoutTypeId", "ContentHash", "ParserVersion", "BuilderOperationId", "NodeCount", "PageCount")
VALUES (:other_work_id, :other_edition_id, 1, 'reader-query-fixture-b', 1, decode(repeat('a2',32),'hex'), 'fixture', :operation_id, 4, 0) RETURNING "Id" AS other_content_id \gset
INSERT INTO "ReaderContentNodes" ("ReaderContentId", "StableId", "OrderIndex", "ReaderNodeTypeId", "ReaderTextBlockTypeId", "CharacterCount")
SELECT content.id, 'p'||i, i-1, 1, 1, 100 FROM (VALUES (:content_id), (:other_content_id)) AS content(id) CROSS JOIN generate_series(1,4) AS i;
INSERT INTO "ReaderContent" ("WorkId", "WorkEditionId", "ContentRevision", "StorageKey", "VariantKey", "ReaderLayoutTypeId", "ContentHash", "ParserVersion", "BuilderOperationId", "NodeCount", "PageCount", "SourcePageCount")
VALUES (:work_id, :edition_id, 1, 'reader-query-fixture-pages', 'pages', 3, decode(repeat('a3',32),'hex'), 'fixture', :operation_id, 0, 3, 3) RETURNING "Id" AS pages_content_id \gset
INSERT INTO "ReaderPages" ("ReaderContentId", "PageIndex", "SourcePageIndex", "TextAnchor")
VALUES (:pages_content_id, 0, 0, 'page-zero'), (:pages_content_id, 1, 1, 'page-one'), (:pages_content_id, 2, 2, 'page-two');
UPDATE "ReaderContent" SET "PublishedAt"=now() WHERE "Id" IN (:content_id,:other_content_id,:pages_content_id);
SET CONSTRAINTS ALL IMMEDIATE;

INSERT INTO "ReaderBookmarks" ("ProfileId", "ReaderContentId", "Locator", "CreatedAt")
VALUES (:profile_id, :content_id, '{"blockId":"p1","characterOffset":0}', now() - INTERVAL '2 minutes')
RETURNING "PublicId" AS older_bookmark_id \gset
INSERT INTO "ReaderBookmarks" ("ProfileId", "ReaderContentId", "Locator", "CreatedAt")
VALUES (:profile_id, :content_id, '{"blockId":"p2","characterOffset":1}', now() - INTERVAL '1 minute')
RETURNING "PublicId" AS newest_bookmark_id \gset
INSERT INTO "ReaderBookmarks" ("ProfileId", "ReaderContentId", "Locator")
VALUES (:profile_id, :other_content_id, '{"blockId":"p3","characterOffset":0}');
INSERT INTO "ReaderBookmarks" ("ProfileId", "ReaderContentId", "Locator")
VALUES (:other_profile_id, :content_id, '{"blockId":"p4","characterOffset":0}');

INSERT INTO "ReaderHighlights" ("ProfileId", "ReaderContentId", "StartLocator", "EndLocator", "SelectedText", "Note", "CreatedAt")
VALUES (:profile_id, :content_id, '{"blockId":"p1","characterOffset":1}', '{"blockId":"p1","characterOffset":2}', 'a', 'First', now() - INTERVAL '2 minutes')
RETURNING "PublicId" AS older_highlight_id \gset
INSERT INTO "ReaderHighlights" ("ProfileId", "ReaderContentId", "StartLocator", "EndLocator", "SelectedText", "Note", "CreatedAt")
VALUES (:profile_id, :content_id, '{"blockId":"p2","characterOffset":3}', '{"blockId":"p2","characterOffset":5}', 'bc', 'Latest', now() - INTERVAL '1 minute')
RETURNING "PublicId" AS newest_highlight_id \gset
INSERT INTO "ReaderHighlights" ("ProfileId", "ReaderContentId", "StartLocator", "EndLocator", "SelectedText", "Note")
VALUES (:profile_id, :other_content_id, '{"blockId":"p1","characterOffset":1}', '{"blockId":"p1","characterOffset":2}', 'a', 'Other work');
INSERT INTO "ReaderHighlights" ("ProfileId", "ReaderContentId", "StartLocator", "EndLocator", "SelectedText", "Note")
VALUES (:other_profile_id, :content_id, '{"blockId":"p1","characterOffset":1}', '{"blockId":"p1","characterOffset":2}', 'a', 'Other profile');

EXECUTE phase_b_reader_bookmarks(:actor_id, :profile_id, :'work_public_id', 1, 0) \gset
SELECT 1 / CASE WHEN :'ReaderBookmarkId'::uuid = :'newest_bookmark_id'::uuid
    AND :'WorkEditionId'::uuid = :'edition_public_id'::uuid
    AND :ContentRevision = 1 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_bookmarks(:actor_id, :profile_id, :'work_public_id', 1, 1) \gset
SELECT 1 / CASE WHEN :'ReaderBookmarkId'::uuid = :'older_bookmark_id'::uuid THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_bookmarks(:actor_id, :profile_id, :'work_public_id', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 2 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_bookmarks(:actor_id, :profile_id, :'other_work_public_id', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;

EXECUTE phase_b_reader_highlights(:actor_id, :profile_id, :'work_public_id', 1, 0) \gset
SELECT 1 / CASE WHEN :'ReaderHighlightId'::uuid = :'newest_highlight_id'::uuid
    AND :'Note' = 'Latest' THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_highlights(:actor_id, :profile_id, :'work_public_id', 1, 1) \gset
SELECT 1 / CASE WHEN :'ReaderHighlightId'::uuid = :'older_highlight_id'::uuid THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_highlights(:actor_id, :profile_id, :'work_public_id', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 2 THEN 1 ELSE 0 END;

EXECUTE phase_b_reader_bookmarks(:other_id, :profile_id, :'work_public_id', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_highlights(:other_id, :profile_id, :'work_public_id', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_bookmarks(:actor_id, :other_profile_id, :'work_public_id', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_bookmarks(:actor_id, :profile_id, :'work_public_id', 101, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_highlights(:actor_id, :profile_id, :'work_public_id', 25, 100001);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;

INSERT INTO "AccountProfiles" ("AccountId", "ProfileId")
VALUES (:other_id, :profile_id);
EXECUTE phase_b_reader_bookmarks(:other_id, :profile_id, :'work_public_id', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 2 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_highlights(:other_id, :profile_id, :'work_public_id', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 2 THEN 1 ELSE 0 END;
DELETE FROM "AccountProfiles"
WHERE "AccountId" = :other_id AND "ProfileId" = :profile_id;
EXECUTE phase_b_reader_bookmarks(:other_id, :profile_id, :'work_public_id', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_highlights(:other_id, :profile_id, :'work_public_id', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;

ANALYZE "ReaderBookmarks";
ANALYZE "ReaderHighlights";
EXPLAIN (ANALYZE, BUFFERS) EXECUTE phase_b_reader_bookmarks(:actor_id, :profile_id, :'work_public_id', 25, 0);
EXPLAIN (ANALYZE, BUFFERS) EXECUTE phase_b_reader_highlights(:actor_id, :profile_id, :'work_public_id', 25, 0);

EXECUTE phase_b_reader_pages(:actor_id, :profile_id, :'edition_public_id', 1, 'pages', 1, 1) \gset
SELECT 1 / CASE WHEN :PageIndex = 1 AND :'TextAnchor' = 'page-one'
    AND :'WorkId'::uuid = :'work_public_id'::uuid THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_pages(:actor_id, :profile_id, :'edition_public_id', 1, 'pages', 0, 25);
SELECT 1 / CASE WHEN :ROW_COUNT = 3 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_pages(:actor_id, :profile_id, :'edition_public_id', 2, 'pages', 0, 25);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_pages(:actor_id, :profile_id, :'edition_public_id', 1, 'pages', -1, 25);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_pages(:actor_id, :profile_id, :'edition_public_id', 1, 'pages', 0, 101);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_pages(:other_id, :profile_id, :'edition_public_id', 1, 'pages', 0, 25);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (:other_id, :profile_id);
EXECUTE phase_b_reader_pages(:other_id, :profile_id, :'edition_public_id', 1, 'pages', 0, 25);
SELECT 1 / CASE WHEN :ROW_COUNT = 3 THEN 1 ELSE 0 END;
DELETE FROM "AccountProfiles" WHERE "AccountId" = :other_id AND "ProfileId" = :profile_id;
EXECUTE phase_b_reader_pages(:other_id, :profile_id, :'edition_public_id', 1, 'pages', 0, 25);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
ANALYZE "ReaderPages";
EXPLAIN (ANALYZE, BUFFERS) EXECUTE phase_b_reader_pages(:actor_id, :profile_id, :'edition_public_id', 1, 'pages', 0, 25);
UPDATE "Accounts" SET "IsEnabled" = FALSE WHERE "Id" = :actor_id;
EXECUTE phase_b_reader_bookmarks(:actor_id, :profile_id, :'work_public_id', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_highlights(:actor_id, :profile_id, :'work_public_id', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_pages(:actor_id, :profile_id, :'edition_public_id', 1, 'pages', 0, 25);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
ROLLBACK;
