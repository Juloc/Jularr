-- Phase B B15 scratch-only: execute canonical bounded Continue SELECT for two profiles.
-- Intentionally committed only inside disposable CI PostgreSQL.
\set ON_ERROR_STOP on
BEGIN;
INSERT INTO "UiLocales" ("Locale","Name") VALUES ('phase-b-continue','Phase B Continue');
INSERT INTO "Accounts" ("Email","DisplayName","AccountRoleTypeId")
VALUES ('phase-b-continue-a@example.invalid','Continue A',1),
       ('phase-b-continue-b@example.invalid','Continue B',2);
INSERT INTO "Profiles" ("OwnerAccountId","DisplayName","UiLocaleId")
SELECT "Id", "DisplayName", (SELECT "Id" FROM "UiLocales" WHERE "Locale" = 'phase-b-continue')
FROM "Accounts" WHERE "Email" LIKE 'phase-b-continue-%@example.invalid';
INSERT INTO "AccountProfiles" ("AccountId","ProfileId")
SELECT p."OwnerAccountId",p."Id" FROM "Profiles" p
WHERE p."DisplayName" IN ('Continue A','Continue B');
COMMIT;

-- Every seeded Progress has exactly one matching Time position, satisfying
-- DEFERRABLE 1:1 child/parent guarantees at COMMIT.
DO $fixture$
DECLARE
    p_a bigint;
    p_b bigint;
    w bigint;
    progress_id bigint;
    i int;
BEGIN
    SELECT "Id" INTO p_a FROM "Profiles" WHERE "DisplayName"='Continue A';
    SELECT "Id" INTO p_b FROM "Profiles" WHERE "DisplayName"='Continue B';
    IF p_a IS NULL OR p_b IS NULL OR p_a = p_b THEN
       RAISE EXCEPTION 'Bad continue fixture Profile identities';
    END IF;
    FOR i IN 1..6 LOOP
       INSERT INTO "Works" ("MediaTypeId","CanonicalTitle")
       VALUES (1,'Phase B Continue A '||i) RETURNING "Id" INTO w;
       INSERT INTO "MediaProgress"
          ("ProfileId","WorkId","ProgressPositionTypeId","IsCompleted","LastActivityAt")
       VALUES (p_a,w,1,i=6,now()-(i*interval '1 minute'))
       RETURNING "Id" INTO progress_id;
       INSERT INTO "TimeProgressPositions" ("MediaProgressId","PositionMs","DurationMs")
       VALUES (progress_id,1000*i,30000);
    END LOOP;
    FOR i IN 1..3 LOOP
       INSERT INTO "Works" ("MediaTypeId","CanonicalTitle")
       VALUES (1,'Phase B Continue B '||i) RETURNING "Id" INTO w;
       INSERT INTO "MediaProgress"
          ("ProfileId","WorkId","ProgressPositionTypeId","LastActivityAt")
       VALUES (p_b,w,1,now()-(i*interval '1 minute'))
       RETURNING "Id" INTO progress_id;
       INSERT INTO "TimeProgressPositions" ("MediaProgressId","PositionMs")
       VALUES (progress_id,1000*i);
    END LOOP;
END $fixture$;

\i /tmp/phase_b_continue_prepared.sql
SELECT "Id" AS profile_a FROM "Profiles" WHERE "DisplayName"='Continue A' \gset
SELECT "Id" AS profile_b FROM "Profiles" WHERE "DisplayName"='Continue B' \gset
SELECT "OwnerAccountId" AS account_a FROM "Profiles" WHERE "Id" = :profile_a \gset
SELECT "OwnerAccountId" AS account_b FROM "Profiles" WHERE "Id" = :profile_b \gset

-- The SAME source SELECT from READ_QUERIES_DRAFT is executed via PREPARE.
-- First expected A item, second A item after offset, and first B item must
-- be distinct and never leak another profile or completed records.
SELECT "PublicId" AS expected_first_a FROM "Works"
WHERE "CanonicalTitle"='Phase B Continue A 1' \gset
SELECT "PublicId" AS expected_second_a FROM "Works"
WHERE "CanonicalTitle"='Phase B Continue A 2' \gset
SELECT "PublicId" AS expected_first_b FROM "Works"
WHERE "CanonicalTitle"='Phase B Continue B 1' \gset
EXECUTE phase_b_continue(:account_a,:profile_a,1,0) \gset
SELECT 1 / CASE WHEN :'WorkId'::uuid = :'expected_first_a'::uuid AND :Revision::bigint=1 THEN 1 ELSE 0 END;
EXECUTE phase_b_continue(:account_a,:profile_a,1,1) \gset
SELECT 1 / CASE WHEN :'WorkId'::uuid = :'expected_second_a'::uuid THEN 1 ELSE 0 END;
EXECUTE phase_b_continue(:account_b,:profile_b,1,0) \gset
SELECT 1 / CASE WHEN :'WorkId'::uuid = :'expected_first_b'::uuid THEN 1 ELSE 0 END;

EXECUTE phase_b_continue(:account_a,:profile_b,25,0);
SELECT 1 / CASE WHEN :ROW_COUNT::bigint = 0 THEN 1 ELSE 0 END;

BEGIN;
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId")
VALUES (:account_b, :profile_a);
EXECUTE phase_b_continue(:account_b,:profile_a,1,0) \gset
SELECT 1 / CASE WHEN :'WorkId'::uuid = :'expected_first_a'::uuid THEN 1 ELSE 0 END;
DELETE FROM "AccountProfiles"
WHERE "AccountId" = :account_b AND "ProfileId" = :profile_a;
EXECUTE phase_b_continue(:account_b,:profile_a,25,0);
SELECT 1 / CASE WHEN :ROW_COUNT::bigint = 0 THEN 1 ELSE 0 END;
UPDATE "Accounts" SET "IsEnabled" = false WHERE "Id" = :account_a;
EXECUTE phase_b_continue(:account_a,:profile_a,25,0);
SELECT 1 / CASE WHEN :ROW_COUNT::bigint = 0 THEN 1 ELSE 0 END;
ROLLBACK;

BEGIN;
SELECT "Id" AS unit_work_id FROM "Works"
WHERE "CanonicalTitle" = 'Phase B Continue A 1' \gset
INSERT INTO "WorkTracks" ("WorkId", "OrderIndex", "DisplayName")
VALUES (:unit_work_id, 0, 'Continue public track')
RETURNING "Id" AS unit_track_id, "PublicId" AS unit_track_public_id \gset
UPDATE "MediaProgress"
SET "WorkTrackId" = :unit_track_id
WHERE "ProfileId" = :profile_a AND "WorkId" = :unit_work_id;
EXECUTE phase_b_continue(:account_a,:profile_a,1,0) \gset
SELECT 1 / CASE WHEN :'WorkTrackId'::uuid = :'unit_track_public_id'::uuid THEN 1 ELSE 0 END;
INSERT INTO "WorkEditions" ("WorkId", "Name")
VALUES (:unit_work_id, 'Continue public edition')
RETURNING "Id" AS unit_edition_id, "PublicId" AS unit_edition_public_id \gset
UPDATE "MediaProgress"
SET "WorkTrackId" = NULL, "WorkEditionId" = :unit_edition_id
WHERE "ProfileId" = :profile_a AND "WorkId" = :unit_work_id;
EXECUTE phase_b_continue(:account_a,:profile_a,1,0) \gset
SELECT 1 / CASE WHEN :'WorkEditionId'::uuid = :'unit_edition_public_id'::uuid THEN 1 ELSE 0 END;
SELECT "PublicId" AS expected_progress_public_id FROM "MediaProgress"
WHERE "ProfileId" = :profile_a AND "WorkId" = :unit_work_id \gset
SELECT 1 / CASE WHEN :'MediaProgressId'::uuid = :'expected_progress_public_id'::uuid THEN 1 ELSE 0 END;
ROLLBACK;

EXPLAIN (ANALYZE, BUFFERS) EXECUTE phase_b_continue(:account_a,:profile_a,25,0);
-- Verify that exactly five uncompleted rows exist for A and three for B.
DO $verify$
BEGIN
 IF (SELECT count(*) FROM "MediaProgress" p JOIN "Profiles" pr ON pr."Id"=p."ProfileId"
      WHERE pr."DisplayName"='Continue A' AND p."IsCompleted"=false)<>5
   OR (SELECT count(*) FROM "MediaProgress" p JOIN "Profiles" pr ON pr."Id"=p."ProfileId"
      WHERE pr."DisplayName"='Continue B' AND p."IsCompleted"=false)<>3 THEN
   RAISE EXCEPTION 'Continue fixture completion/profiles incorrect';
 END IF;
END $verify$;
DEALLOCATE phase_b_continue;
