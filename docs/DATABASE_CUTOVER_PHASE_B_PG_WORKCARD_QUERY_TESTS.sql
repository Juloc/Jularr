-- Phase B B15: bounded Watchlist WorkCard read against seeded scratch rows.
-- This validates that the REAL draft SELECT parses and executes on PostgreSQL.
-- The dataset is synthetic and intentionally not a production performance signoff.
-- /tmp/phase_b_watchlist_prepared.sql is built directly from the canonical draft
-- by the disposable PostgreSQL GitHub Actions workflow. Never run on dev.
BEGIN;
SET LOCAL statement_timeout = '20s';

INSERT INTO "UiLocales" ("Locale","Name") VALUES ('en','English');
INSERT INTO "Accounts" ("Email","DisplayName","AccountRoleTypeId")
VALUES ('phase-b-watchlist@example.invalid','Watchlist Query Fixture',1);
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('phase-b-watchlist-other@example.invalid', 'Watchlist Other Account', 2);
SELECT "Id" AS other_account_id FROM "Accounts"
WHERE "Email" = 'phase-b-watchlist-other@example.invalid' \gset
SELECT "Id" AS fixture_account_id FROM "Accounts"
WHERE "Email" = 'phase-b-watchlist@example.invalid' \gset
INSERT INTO "Profiles" ("OwnerAccountId","DisplayName","UiLocaleId")
SELECT :fixture_account_id, 'Watchlist Query Profile', "Id"
FROM "UiLocales" WHERE "Locale" = 'en';
SELECT "Id" AS fixture_profile_id FROM "Profiles"
WHERE "OwnerAccountId" = :fixture_account_id
  AND "DisplayName" = 'Watchlist Query Profile' \gset
INSERT INTO "AccountProfiles" ("AccountId","ProfileId")
VALUES (:fixture_account_id,:fixture_profile_id);

INSERT INTO "Works" ("MediaTypeId","CanonicalTitle")
SELECT 1, 'Phase B WorkCard ' || g::text
FROM generate_series(1,1000) AS g;
INSERT INTO "WatchlistEntries" ("ProfileId","WorkId","AddedAt")
SELECT :fixture_profile_id, w."Id", now() - (w."Id" * interval '1 second')
FROM "Works" AS w
WHERE w."CanonicalTitle" LIKE 'Phase B WorkCard %';

SELECT
    "Id" AS first_work_id,
    "PublicId" AS first_work_public_id
FROM "Works"
WHERE "CanonicalTitle" LIKE 'Phase B WorkCard %'
ORDER BY "Id"
LIMIT 1 \gset
INSERT INTO "ImageTypes" ("Id", "Key") VALUES (1, 'cover'), (2, 'banner');
INSERT INTO "ImageTypeTargets" ("ImageTypeId", "ImageTargetKindTypeId") VALUES (1, 1), (2, 1);
INSERT INTO "Images" ("StorageKey", "MimeType")
VALUES ('phase-b-watchlist-cover', 'image/png')
RETURNING "Id" AS cover_id, "PublicId" AS cover_public_id \gset
INSERT INTO "Images" ("StorageKey", "MimeType")
VALUES ('phase-b-watchlist-banner', 'image/png')
RETURNING "Id" AS banner_id, "PublicId" AS banner_public_id \gset
INSERT INTO "ImageAssignments" ("ImageId", "ImageTypeId", "ImageTargetKindTypeId", "WorkId")
VALUES (:cover_id, 1, 1, :first_work_id), (:banner_id, 2, 1, :first_work_id);

-- Exactly 1000 root rows, without creating any media-file/album duplications.
DO $assert$
BEGIN
    IF (SELECT count(*) FROM "WatchlistEntries" AS w
        JOIN "Profiles" AS p ON p."Id" = w."ProfileId"
        JOIN "Accounts" AS a ON a."Id" = p."OwnerAccountId"
        WHERE a."Email" = 'phase-b-watchlist@example.invalid') <> 1000 THEN
        RAISE EXCEPTION 'Expected exactly 1000 local root rows';
    END IF;
END $assert$;

\i /tmp/phase_b_watchlist_prepared.sql

EXECUTE phase_b_watchlist(:fixture_account_id,:fixture_profile_id,'en',1,0) \gset
SELECT 1 / CASE WHEN :'WorkId'::uuid = :'first_work_public_id'::uuid
    AND :'CoverImageId'::uuid = :'cover_public_id'::uuid
    AND :'BannerImageId'::uuid = :'banner_public_id'::uuid THEN 1 ELSE 0 END;

-- The same source SELECT that will be used by the Service. No NAS/provider I/O.
EXPLAIN (ANALYZE, BUFFERS) EXECUTE phase_b_watchlist(:fixture_account_id,:fixture_profile_id,'en',25,0);
EXECUTE phase_b_watchlist(:fixture_account_id,:fixture_profile_id,'en',25,0);
SELECT 1 / CASE WHEN :ROW_COUNT::bigint = 25 THEN 1 ELSE 0 END;
EXECUTE phase_b_watchlist(:other_account_id,:fixture_profile_id,'en',25,0);
SELECT 1 / CASE WHEN :ROW_COUNT::bigint = 0 THEN 1 ELSE 0 END;
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId")
VALUES (:other_account_id, :fixture_profile_id);
EXECUTE phase_b_watchlist(:other_account_id,:fixture_profile_id,'en',25,0);
SELECT 1 / CASE WHEN :ROW_COUNT::bigint = 25 THEN 1 ELSE 0 END;
DELETE FROM "AccountProfiles"
WHERE "AccountId" = :other_account_id AND "ProfileId" = :fixture_profile_id;
EXECUTE phase_b_watchlist(:other_account_id,:fixture_profile_id,'en',25,0);
SELECT 1 / CASE WHEN :ROW_COUNT::bigint = 0 THEN 1 ELSE 0 END;
UPDATE "Accounts" SET "IsEnabled" = false WHERE "Id" = :fixture_account_id;
EXECUTE phase_b_watchlist(:fixture_account_id,:fixture_profile_id,'en',25,0);
SELECT 1 / CASE WHEN :ROW_COUNT::bigint = 0 THEN 1 ELSE 0 END;
DEALLOCATE phase_b_watchlist;
ROLLBACK;
