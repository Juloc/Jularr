\set ON_ERROR_STOP on
BEGIN;
SET LOCAL statement_timeout = '20s';
\i /tmp/phase_b_acquisition_requests_prepared.sql

INSERT INTO "UiLocales" ("Locale", "Name")
VALUES ('phase-b-request-read', 'Request fixture') RETURNING "Id" AS locale_id \gset
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('request-reader-a@example.invalid', 'Request actor', 1) RETURNING "Id" AS actor_id \gset
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('request-reader-b@example.invalid', 'Request other', 2) RETURNING "Id" AS other_id \gset
INSERT INTO "Profiles" ("OwnerAccountId", "DisplayName", "UiLocaleId")
VALUES (:actor_id, 'Request primary', :locale_id) RETURNING "Id" AS profile_id \gset
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (:actor_id, :profile_id);
INSERT INTO "Profiles" ("OwnerAccountId", "DisplayName", "UiLocaleId")
VALUES (:other_id, 'Request secondary', :locale_id) RETURNING "Id" AS other_profile_id \gset
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (:other_id, :other_profile_id);

INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle")
VALUES (1, 'Request test work 1') RETURNING "Id" AS work_a_id, "PublicId" AS work_a_public \gset
INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle")
VALUES (1, 'Request test work 2') RETURNING "Id" AS work_b_id \gset
INSERT INTO "Operations" ("OperationKindKey", "OperationStatusTypeId")
VALUES ('request-fixture-operation', 1) RETURNING "Id" AS operation_id, "PublicId" AS operation_public \gset

INSERT INTO "AcquisitionRequests" ("ProfileId", "WorkId", "AcquisitionRequestStatusTypeId", "CreatedAt")
VALUES (:profile_id, :work_a_id, 1, now() - INTERVAL '2 minutes')
RETURNING "PublicId" AS older_request_public \gset
INSERT INTO "AcquisitionRequests" ("ProfileId", "WorkId", "AcquisitionRequestStatusTypeId", "OperationId", "CreatedAt")
VALUES (:profile_id, :work_a_id, 2, :operation_id, now() - INTERVAL '1 minute')
RETURNING "PublicId" AS newer_request_public \gset
INSERT INTO "AcquisitionRequests" ("ProfileId", "WorkId", "AcquisitionRequestStatusTypeId")
VALUES (:other_profile_id, :work_b_id, 1);

EXECUTE phase_b_acquisition_requests(:actor_id, :profile_id, 1, 0) \gset
SELECT 1 / CASE WHEN :'AcquisitionRequestId'::uuid = :'newer_request_public'::uuid
    AND :'WorkId'::uuid = :'work_a_public'::uuid
    AND :'OperationId'::uuid = :'operation_public'::uuid
    AND :AcquisitionRequestStatusTypeId = 2 THEN 1 ELSE 0 END;
EXECUTE phase_b_acquisition_requests(:actor_id, :profile_id, 1, 1) \gset
SELECT 1 / CASE WHEN :'AcquisitionRequestId'::uuid = :'older_request_public'::uuid THEN 1 ELSE 0 END;
EXECUTE phase_b_acquisition_requests(:actor_id, :profile_id, 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 2 THEN 1 ELSE 0 END;
EXECUTE phase_b_acquisition_requests(:other_id, :profile_id, 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_acquisition_requests(:actor_id, :other_profile_id, 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_acquisition_requests(:actor_id, :profile_id, 101, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_acquisition_requests(:actor_id, :profile_id, 25, 100001);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;

INSERT INTO "AccountProfiles" ("AccountId", "ProfileId")
VALUES (:other_id, :profile_id);
EXECUTE phase_b_acquisition_requests(:other_id, :profile_id, 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 2 THEN 1 ELSE 0 END;
DELETE FROM "AccountProfiles"
WHERE "AccountId" = :other_id AND "ProfileId" = :profile_id;
EXECUTE phase_b_acquisition_requests(:other_id, :profile_id, 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;

ANALYZE "AcquisitionRequests";
EXPLAIN (ANALYZE, BUFFERS) EXECUTE phase_b_acquisition_requests(:actor_id, :profile_id, 25, 0);
UPDATE "Accounts" SET "IsEnabled" = FALSE WHERE "Id" = :actor_id;
EXECUTE phase_b_acquisition_requests(:actor_id, :profile_id, 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
ROLLBACK;
