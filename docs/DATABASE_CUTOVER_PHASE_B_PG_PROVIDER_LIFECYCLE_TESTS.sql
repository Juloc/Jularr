\set ON_ERROR_STOP on
BEGIN;
SET LOCAL statement_timeout = '20s';
\o /dev/null
\i /tmp/phase_b_provider_grants_prepared.sql
\i /tmp/phase_b_provider_candidates_prepared.sql
\i /tmp/phase_b_provider_advance_prepared.sql
SELECT now() AS test_now \gset
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('provider-owner@example.invalid', 'Provider Owner', 1) RETURNING "Id" AS owner_id \gset
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('provider-other@example.invalid', 'Provider Media Manager', 3) RETURNING "Id" AS other_id \gset
INSERT INTO "UiLocales" ("Locale", "Name") VALUES ('phase-b-provider', 'Provider') RETURNING "Id" AS locale_id \gset
INSERT INTO "Profiles" ("OwnerAccountId", "DisplayName", "UiLocaleId")
VALUES (:owner_id, 'Provider profile', :locale_id) RETURNING "Id" AS profile_id \gset
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (:owner_id, :profile_id);
INSERT INTO "Providers" ("Key") VALUES ('plex') RETURNING "Id" AS provider_id \gset
INSERT INTO "PlexSettings" ("ProviderId", "ClientIdentifier") VALUES (:provider_id, 'phase-b-provider');
SELECT 1 / CASE WHEN NOT "IsAccountLoginEnabled" AND NOT "IsAccountLinkEnabled" AND NOT "IsAccountAutoProvisionEnabled" THEN 1 ELSE 0 END
FROM "Providers" WHERE "Id" = :provider_id;
SELECT 1 / CASE WHEN "RequireApproval" THEN 1 ELSE 0 END FROM "PlexSettings" WHERE "ProviderId" = :provider_id;
INSERT INTO "ProviderMediaConnections" ("ProfileId", "ProviderId", "ExternalAccountId", "ConsentedAt", "CredentialsStorageKey")
VALUES (:profile_id, :provider_id, 'verified-fixture', :'test_now', 'phase_b_protected_reference');
SELECT 1 / CASE WHEN NOT "IsWatchlistSyncEnabled" AND NOT "IsProgressSyncEnabled" THEN 1 ELSE 0 END
FROM "ProviderMediaConnections" WHERE "ProfileId" = :profile_id;
INSERT INTO "ProviderServerGrants" ("ProviderId", "ExternalServerId", "DisplayName", "CredentialsStorageKey", "ApprovedByAccountId", "ApprovedEndpoint", "VerifiedOwnerExternalAccountId")
SELECT :provider_id, 'server-' || number, 'Fixture ' || number, 'phase_b_protected_reference', :owner_id, 'https://server.example.invalid/plex', 'verified-owner-fixture'
FROM generate_series(1, 1000) AS number;
SELECT "Id" AS grant_id FROM "ProviderServerGrants" WHERE "ExternalServerId" = 'server-1' \gset
INSERT INTO "ProviderGrantLibrarySections" ("ProviderServerGrantId", "ExternalSectionId")
SELECT "Id", 'movies' FROM "ProviderServerGrants" WHERE "ProviderId" = :provider_id;
EXECUTE phase_b_provider_grants(:owner_id, 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 25 THEN 1 ELSE 0 END;
EXECUTE phase_b_provider_grants(:owner_id, 25, 1000);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_provider_grants(:other_id, 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_provider_grants(:owner_id, 101, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (1, 'Provider fixture') RETURNING "Id" AS work_id, "PublicId" AS work_public_id \gset
INSERT INTO "Operations" ("OperationKindKey", "OperationStatusTypeId", "ClaimedBy", "ClaimedUntil")
VALUES ('provider_catalog_scan', 2, 'phase-b-provider-worker', now() + INTERVAL '20 minutes') RETURNING "Id" AS operation_id \gset
INSERT INTO "ProviderCatalogCheckpoints" ("ProviderServerGrantId", "ExternalSectionId", "NextStart", "PageSize", "GrantRevision", "OperationId", "RequestedByAccountId")
VALUES (:grant_id, 'movies', 0, 200, 1, :operation_id, :owner_id);
INSERT INTO "ProviderCatalogItems" ("ProviderServerGrantId", "ExternalItemKey", "ExternalSectionId", "Generation", "WorkId", "SourcePageStart", "VerifiedAt")
VALUES (:grant_id, 'item-1', 'movies', 1, :work_id, 0, :'test_now');
DO $unaccepted_page$
DECLARE
    rejected text;
BEGIN
    BEGIN
        SET CONSTRAINTS "TR_ProviderCatalogItems_AcceptedPage" IMMEDIATE;
        RAISE EXCEPTION 'Catalog page became durable without cursor acceptance';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected = CONSTRAINT_NAME;
        IF rejected <> 'CK_ProviderCatalogItems_AcceptedPage' THEN RAISE; END IF;
    END;
END $unaccepted_page$;
EXECUTE phase_b_provider_advance(:grant_id, 'movies', 1, 1, 0, 200, 201, :'test_now', 'foreign-worker');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_provider_advance(:grant_id, 'movies', 1, 1, 0, 200, 201, :'test_now', 'phase-b-provider-worker');
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
SET CONSTRAINTS ALL IMMEDIATE;
SET CONSTRAINTS ALL DEFERRED;
EXECUTE phase_b_provider_advance(:grant_id, 'movies', 1, 1, 0, 200, 201, :'test_now', 'phase-b-provider-worker');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_provider_candidates(:owner_id, :profile_id, :'work_public_id', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
UPDATE "Providers" SET "IsMediaConnectionEnabled" = TRUE WHERE "Id" = :provider_id;
EXECUTE phase_b_provider_candidates(:owner_id, :profile_id, :'work_public_id', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
EXECUTE phase_b_provider_candidates(:other_id, :profile_id, :'work_public_id', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
UPDATE "ProviderMediaConnections" SET "RevokedAt" = :'test_now', "Revision" = 2 WHERE "ProfileId" = :profile_id;
EXECUTE phase_b_provider_candidates(:owner_id, :profile_id, :'work_public_id', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
UPDATE "ProviderMediaConnections" SET "CredentialsStorageKey" = 'phase_b_staged_reference', "RevokedAt" = NULL, "Revision" = "Revision" + 1
WHERE "ProfileId" = :profile_id AND "Revision" = 1;
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
UPDATE "ProviderMediaConnections" SET "RevokedAt" = NULL WHERE "ProfileId" = :profile_id;
UPDATE "ProviderGrantLibrarySections" SET "IsEnabled" = FALSE WHERE "ProviderServerGrantId" = :grant_id;
EXECUTE phase_b_provider_candidates(:owner_id, :profile_id, :'work_public_id', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_provider_advance(:grant_id, 'movies', 1, 1, 200, NULL, 201, :'test_now', 'phase-b-provider-worker');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
UPDATE "ProviderGrantLibrarySections" SET "IsEnabled" = TRUE WHERE "ProviderServerGrantId" = :grant_id;
UPDATE "ProviderServerGrants" SET "RevokedAt" = :'test_now' WHERE "Id" = :grant_id;
EXECUTE phase_b_provider_candidates(:owner_id, :profile_id, :'work_public_id', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_provider_advance(:grant_id, 'movies', 1, 1, 200, NULL, 201, :'test_now', 'phase-b-provider-worker');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
UPDATE "ProviderServerGrants" SET "RevokedAt" = NULL WHERE "Id" = :grant_id;
UPDATE "Operations" SET "ClaimedUntil" = :'test_now' WHERE "Id" = :operation_id;
EXECUTE phase_b_provider_advance(:grant_id, 'movies', 1, 1, 200, NULL, 201, :'test_now', 'phase-b-provider-worker');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
UPDATE "Operations" SET "ClaimedUntil" = now() + INTERVAL '20 minutes' WHERE "Id" = :operation_id;
UPDATE "Accounts" SET "AccountRoleTypeId" = 3 WHERE "Id" = :owner_id;
EXECUTE phase_b_provider_advance(:grant_id, 'movies', 1, 1, 200, NULL, 201, :'test_now', 'phase-b-provider-worker');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_provider_grants(:owner_id, 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
UPDATE "Accounts" SET "AccountRoleTypeId" = 1 WHERE "Id" = :owner_id;
UPDATE "ProviderServerGrants" SET "Revision" = 2 WHERE "Id" = :grant_id;
EXECUTE phase_b_provider_candidates(:owner_id, :profile_id, :'work_public_id', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_provider_advance(:grant_id, 'movies', 1, 1, 200, NULL, 201, :'test_now', 'phase-b-provider-worker');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;

DELETE FROM "ProviderCatalogItems" WHERE "ProviderServerGrantId" = :grant_id;
UPDATE "ProviderCatalogCheckpoints" SET "Generation" = 2, "GrantRevision" = 2, "NextStart" = 0, "PagesScanned" = 0, "LastPageStart" = NULL
WHERE "ProviderServerGrantId" = :grant_id;
EXECUTE phase_b_provider_advance(:grant_id, 'movies', 1, 1, 0, NULL, 1, :'test_now', 'phase-b-provider-worker');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
SAVEPOINT lost_page;
INSERT INTO "ProviderCatalogItems" ("ProviderServerGrantId", "ExternalItemKey", "ExternalSectionId", "Generation", "WorkId", "SourcePageStart", "VerifiedAt")
VALUES (:grant_id, 'rolled-back-item', 'movies', 2, :work_id, 0, :'test_now');
EXECUTE phase_b_provider_advance(:grant_id, 'movies', 2, 2, 0, NULL, 1, :'test_now', 'phase-b-provider-worker');
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
ROLLBACK TO SAVEPOINT lost_page;
SELECT 1 / CASE WHEN NOT EXISTS (SELECT 1 FROM "ProviderCatalogItems" WHERE "ProviderServerGrantId" = :grant_id) THEN 1 ELSE 0 END;
SELECT 1 / CASE WHEN "NextStart" = 0 AND "PagesScanned" = 0 THEN 1 ELSE 0 END FROM "ProviderCatalogCheckpoints" WHERE "ProviderServerGrantId" = :grant_id;
INSERT INTO "ProviderCatalogItems" ("ProviderServerGrantId", "ExternalItemKey", "ExternalSectionId", "Generation", "WorkId", "SourcePageStart", "VerifiedAt")
VALUES (:grant_id, 'item-2', 'movies', 2, :work_id, 0, :'test_now');
EXECUTE phase_b_provider_advance(:grant_id, 'movies', 2, 2, 0, NULL, 1, :'test_now', 'phase-b-provider-worker');
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
SET CONSTRAINTS ALL IMMEDIATE;
EXECUTE phase_b_provider_candidates(:owner_id, :profile_id, :'work_public_id', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
DO $provider_constraints$
DECLARE
    grant_id bigint;
    provider_id bigint;
    rejected text;
    endpoint text;
BEGIN
    SELECT "Id", "ProviderId" INTO grant_id, provider_id FROM "ProviderServerGrants" WHERE "ExternalServerId" = 'server-1';
    FOREACH endpoint IN ARRAY ARRAY['http://server.example.invalid', 'https://secret@server.example.invalid', 'https://server.example.invalid?token=x', 'https://server.example.invalid#secret']
    LOOP
        BEGIN
            UPDATE "ProviderServerGrants" SET "ApprovedEndpoint" = endpoint WHERE "Id" = grant_id;
            RAISE EXCEPTION 'Credential-bearing/unsafe endpoint was accepted';
        EXCEPTION WHEN check_violation THEN
            GET STACKED DIAGNOSTICS rejected = CONSTRAINT_NAME;
            IF rejected <> 'CK_ProviderServerGrants_Endpoint' THEN RAISE; END IF;
        END;
    END LOOP;
    BEGIN
        UPDATE "ProviderServerGrants" SET "CredentialsStorageKey" = '/data/secret.json' WHERE "Id" = grant_id;
        RAISE EXCEPTION 'Path-shaped credential reference was accepted';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected = CONSTRAINT_NAME;
        IF rejected <> 'CK_ProviderServerGrants_OpaqueCredential' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "ProviderCatalogItems" SET "Generation" = 1 WHERE "ProviderServerGrantId" = grant_id;
        RAISE EXCEPTION 'Previous generation cache was accepted';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected = CONSTRAINT_NAME;
        IF rejected <> 'FK_ProviderCatalogItems_Checkpoint' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "ProviderCatalogCheckpoints" SET "PageSize" = 201 WHERE "ProviderServerGrantId" = grant_id;
        RAISE EXCEPTION 'Unbounded provider source page was accepted';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected = CONSTRAINT_NAME;
        IF rejected <> 'CK_ProviderCatalogCheckpoints_PageSize' THEN RAISE; END IF;
    END;
END $provider_constraints$;
INSERT INTO "ProviderCatalogCheckpoints" ("ProviderServerGrantId", "ExternalSectionId", "NextStart", "PageSize", "GrantRevision", "OperationId", "RequestedByAccountId", "PagesScanned", "LastPageStart", "CompletedAt")
SELECT "Id", 'movies', NULL, 200, 1, :operation_id, :owner_id, 1, 0, now()
FROM "ProviderServerGrants" WHERE "ProviderId" = :provider_id AND "Id" <> :grant_id;
INSERT INTO "ProviderCatalogItems" ("ProviderServerGrantId", "ExternalItemKey", "ExternalSectionId", "Generation", "WorkId", "SourcePageStart", "VerifiedAt")
SELECT "Id", 'fixture-item', 'movies', 1, :work_id, 0, :'test_now'
FROM "ProviderServerGrants" WHERE "ProviderId" = :provider_id AND "Id" <> :grant_id;
EXECUTE phase_b_provider_candidates(:owner_id, :profile_id, :'work_public_id', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 25 THEN 1 ELSE 0 END;
EXECUTE phase_b_provider_candidates(:owner_id, :profile_id, :'work_public_id', 100, 900);
SELECT 1 / CASE WHEN :ROW_COUNT = 100 THEN 1 ELSE 0 END;
EXECUTE phase_b_provider_candidates(:owner_id, :profile_id, :'work_public_id', 25, 1000);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_provider_candidates(:owner_id, :profile_id, :'work_public_id', 101, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_provider_candidates(:other_id, :profile_id, :'work_public_id', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (1, 'Other provider Work') RETURNING "Id" AS other_work_id \gset
INSERT INTO "ProviderCatalogItems" ("ProviderServerGrantId", "ExternalItemKey", "ExternalSectionId", "Generation", "WorkId", "SourcePageStart", "VerifiedAt")
SELECT "Id", 'unrelated-item', 'movies', 1, :other_work_id, 0, :'test_now'
FROM "ProviderServerGrants" WHERE "ProviderId" = :provider_id AND "Id" <> :grant_id;
EXECUTE phase_b_provider_candidates(:owner_id, :profile_id, :'work_public_id', 100, 1000);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
ANALYZE "ProviderServerGrants";
ANALYZE "ProviderGrantLibrarySections";
ANALYZE "ProviderCatalogCheckpoints";
ANALYZE "ProviderCatalogItems";
\o
EXPLAIN (ANALYZE, BUFFERS) EXECUTE phase_b_provider_grants(:owner_id, 25, 0);
EXPLAIN (ANALYZE, BUFFERS) EXECUTE phase_b_provider_candidates(:owner_id, :profile_id, :'work_public_id', 25, 0);
ROLLBACK;
