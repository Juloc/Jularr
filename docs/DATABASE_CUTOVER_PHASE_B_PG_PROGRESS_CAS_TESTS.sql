-- Phase B B03 isolated PG evidence: optimistic revision and offline event idempotency.
-- Uses the exact Logic-only CAS query extracted from READ_QUERIES_DRAFT.
-- Fixture from prior Continue test; run only on disposable phase_b_scratch.
\set ON_ERROR_STOP on
\i /tmp/phase_b_progress_cas_prepared.sql

SELECT p."Id" AS cas_progress, p."ProfileId" AS cas_profile
FROM "MediaProgress" p
JOIN "Works" w ON w."Id"=p."WorkId"
WHERE w."CanonicalTitle"='Phase B Continue A 1' \gset
SELECT "Id" AS other_profile FROM "Profiles"
WHERE "DisplayName"='Continue B' \gset
SELECT "OwnerAccountId" AS cas_account FROM "Profiles" WHERE "Id" = :cas_profile \gset
SELECT "OwnerAccountId" AS other_account FROM "Profiles" WHERE "Id" = :other_profile \gset
SELECT gen_random_uuid() AS client_event_id \gset

-- Correct scope + expected revision: exactly one accepted revision.
BEGIN;
EXECUTE phase_b_progress_cas(:cas_progress,:cas_account,:cas_profile,1) \gset
SELECT 1 / CASE WHEN :Revision::bigint=2 THEN 1 ELSE 0 END;
INSERT INTO "MediaProgressCheckpointEvents"
 ("MediaProgressId","ProfileId","ClientEventId","AcceptedRevision")
VALUES (:cas_progress,:cas_profile,:'client_event_id'::uuid,2);
COMMIT;

-- Replayed older revision and a different authenticated Profile must be
-- zero-row changes, not optimistic overwrites of the accepted checkpoint.
EXECUTE phase_b_progress_cas(:cas_progress,:cas_account,:cas_profile,1);
SELECT 1 / CASE WHEN :ROW_COUNT::bigint = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_cas(:cas_progress,:cas_account,:other_profile,2);
SELECT 1 / CASE WHEN :ROW_COUNT::bigint = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_cas(:cas_progress,:other_account,:cas_profile,2);
SELECT 1 / CASE WHEN :ROW_COUNT::bigint = 0 THEN 1 ELSE 0 END;

BEGIN;
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId")
VALUES (:other_account, :cas_profile);
EXECUTE phase_b_progress_cas(:cas_progress,:other_account,:cas_profile,2) \gset
SELECT 1 / CASE WHEN :Revision::bigint = 3 THEN 1 ELSE 0 END;
DELETE FROM "AccountProfiles"
WHERE "AccountId" = :other_account AND "ProfileId" = :cas_profile;
EXECUTE phase_b_progress_cas(:cas_progress,:other_account,:cas_profile,3);
SELECT 1 / CASE WHEN :ROW_COUNT::bigint = 0 THEN 1 ELSE 0 END;
UPDATE "Accounts" SET "IsEnabled" = false WHERE "Id" = :cas_account;
EXECUTE phase_b_progress_cas(:cas_progress,:cas_account,:cas_profile,3);
SELECT 1 / CASE WHEN :ROW_COUNT::bigint = 0 THEN 1 ELSE 0 END;
ROLLBACK;
DO $check$
DECLARE
  v_progress bigint;
  v_profile bigint;
  v_other bigint;
  v_event uuid;
BEGIN
  SELECT p."Id",p."ProfileId" INTO v_progress,v_profile
  FROM "MediaProgress" p JOIN "Works" w ON w."Id"=p."WorkId"
  WHERE w."CanonicalTitle"='Phase B Continue A 1';
  SELECT "Id" INTO v_other FROM "Profiles" WHERE "DisplayName"='Continue B';
  IF (SELECT "Revision" FROM "MediaProgress" WHERE "Id"=v_progress)<>2 THEN
    RAISE EXCEPTION 'Stale or foreign Profile overwrote optimistic progress';
  END IF;
  IF (SELECT count(*) FROM "MediaProgressCheckpointEvents" WHERE "MediaProgressId"=v_progress
       AND "ProfileId"=v_profile AND "AcceptedRevision"=2)<>1 THEN
    RAISE EXCEPTION 'Missing accepted idempotency checkpoint';
  END IF;
  SELECT "ClientEventId" INTO v_event FROM "MediaProgressCheckpointEvents"
  WHERE "MediaProgressId"=v_progress AND "AcceptedRevision"=2;
  BEGIN
    INSERT INTO "MediaProgressCheckpointEvents"
      ("ProfileId","MediaProgressId","ClientEventId","AcceptedRevision")
    VALUES (v_profile,v_progress,v_event,3);
    RAISE EXCEPTION 'Duplicate offline event was accepted';
  EXCEPTION WHEN unique_violation THEN NULL;
  END;
  BEGIN
    INSERT INTO "MediaProgressCheckpointEvents"
      ("ProfileId","MediaProgressId","ClientEventId","AcceptedRevision")
    VALUES (v_other,v_progress,gen_random_uuid(),3);
    RAISE EXCEPTION 'Cross-Profile offline checkpoint was accepted';
  EXCEPTION WHEN foreign_key_violation THEN NULL;
  END;
END $check$;
DEALLOCATE phase_b_progress_cas;
