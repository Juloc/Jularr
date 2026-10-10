\set ON_ERROR_STOP on
BEGIN;
SET LOCAL statement_timeout = '20s';
\i /tmp/phase_b_reader_bookmarks_prepared.sql
\i /tmp/phase_b_reader_highlights_prepared.sql

INSERT INTO "UiLocales" ("Locale", "Name")
VALUES ('phase-b-reader-queries', 'Phase B reader') RETURNING "Id" AS locale_id \gset;
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('reader-actor@example.invalid', 'Reader actor', 1) RETURNING "Id" AS actor_id \gset;
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('reader-other@example.invalid', 'Reader other', 2) RETURNING "Id" AS other_id \gset;
INSERT INTO "Profiles" ("OwnerAccountId", "DisplayName", "UiLocaleId")
VALUES (:actor_id, 'Reader primary', :locale_id) RETURNING "Id" AS profile_id \gset;
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (:actor_id, :profile_id);
INSERT INTO "Profiles" ("OwnerAccountId", "DisplayName", "UiLocaleId")
VALUES (:other_id, 'Reader secondary', :locale_id) RETURNING "Id" AS other_profile_id \gset;
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (:other_id, :other_profile_id);

INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle")
VALUES (4, 'Reader query work A') RETURNING "Id" AS work_id, "PublicId" AS work_public_id \gset;
INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle")
VALUES (4, 'Reader query work B') RETURNING "Id" AS other_work_id, "PublicId" AS other_work_public_id \gset;
INSERT INTO "WorkEditions" ("WorkId", "Name")
VALUES (:work_id, 'Reader query edition A') RETURNING "Id" AS edition_id, "PublicId" AS edition_public_id \gset;
INSERT INTO "WorkEditions" ("WorkId", "Name")
VALUES (:other_work_id, 'Reader query edition B') RETURNING "Id" AS other_edition_id \gset;
INSERT INTO "ReaderContent" ("WorkId", "WorkEditionId", "ContentRevision", "StorageKey")
VALUES (:work_id, :edition_id, 1, 'reader-query-fixture-a') RETURNING "Id" AS content_id \gset;
INSERT INTO "ReaderContent" ("WorkId", "WorkEditionId", "ContentRevision", "StorageKey")
VALUES (:other_work_id, :other_edition_id, 1, 'reader-query-fixture-b') RETURNING "Id" AS other_content_id \gset;

INSERT INTO "ReaderBookmarks" ("ProfileId", "ReaderContentId", "TextLocator", "CreatedAt")
VALUES (:profile_id, :content_id, '{"paragraph":1}', now() - INTERVAL '2 minutes')
RETURNING "PublicId" AS older_bookmark_id \gset;
INSERT INTO "ReaderBookmarks" ("ProfileId", "ReaderContentId", "TextLocator", "CreatedAt")
VALUES (:profile_id, :content_id, '{"paragraph":2,"text":"quote''; --"}', now() - INTERVAL '1 minute')
RETURNING "PublicId" AS newest_bookmark_id \gset;
INSERT INTO "ReaderBookmarks" ("ProfileId", "ReaderContentId", "TextLocator")
VALUES (:profile_id, :other_content_id, '{"paragraph":3}');
INSERT INTO "ReaderBookmarks" ("ProfileId", "ReaderContentId", "TextLocator")
VALUES (:other_profile_id, :content_id, '{"paragraph":4}');

INSERT INTO "ReaderHighlights" ("ProfileId", "ReaderContentId", "StartLocator", "EndLocator", "Note", "CreatedAt")
VALUES (:profile_id, :content_id, '{"offset":1}', '{"offset":2}', 'First', now() - INTERVAL '2 minutes')
RETURNING "PublicId" AS older_highlight_id \gset;
INSERT INTO "ReaderHighlights" ("ProfileId", "ReaderContentId", "StartLocator", "EndLocator", "Note", "CreatedAt")
VALUES (:profile_id, :content_id, '{"offset":3}', '{"offset":5}', 'Latest', now() - INTERVAL '1 minute')
RETURNING "PublicId" AS newest_highlight_id \gset;
INSERT INTO "ReaderHighlights" ("ProfileId", "ReaderContentId", "StartLocator", "EndLocator", "Note")
VALUES (:profile_id, :other_content_id, '{"offset":1}', '{"offset":2}', 'Other work');
INSERT INTO "ReaderHighlights" ("ProfileId", "ReaderContentId", "StartLocator", "EndLocator", "Note")
VALUES (:other_profile_id, :content_id, '{"offset":1}', '{"offset":2}', 'Other profile');

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
UPDATE "Accounts" SET "IsEnabled" = FALSE WHERE "Id" = :actor_id;
EXECUTE phase_b_reader_bookmarks(:actor_id, :profile_id, :'work_public_id', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_reader_highlights(:actor_id, :profile_id, :'work_public_id', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
ROLLBACK;
