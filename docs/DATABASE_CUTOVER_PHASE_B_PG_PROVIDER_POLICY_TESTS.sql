\set ON_ERROR_STOP on
BEGIN;
SET LOCAL statement_timeout = '20s';
\i /tmp/phase_b_provider_flow_prepared.sql
\i /tmp/phase_b_provider_profile_media_prepared.sql

INSERT INTO "UiLocales" ("Locale", "Name")
VALUES ('phase-b-provider-policy', 'Provider policy') RETURNING "Id" AS locale_id \gset
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('provider-owner@example.invalid', 'Provider owner', 1) RETURNING "Id" AS owner_id \gset
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('provider-outsider@example.invalid', 'Provider outsider', 2) RETURNING "Id" AS outsider_id \gset
INSERT INTO "Profiles" ("OwnerAccountId", "DisplayName", "UiLocaleId")
VALUES (:owner_id, 'Provider profile', :locale_id) RETURNING "Id" AS profile_id \gset
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (:owner_id, :profile_id);
INSERT INTO "Providers" ("Key")
VALUES ('phase-b-provider-policy') RETURNING "Id" AS provider_id \gset

SELECT 1 / CASE WHEN
    NOT "IsAccountLoginEnabled"
    AND NOT "IsAccountLinkEnabled"
    AND NOT "IsMediaConnectionEnabled"
    AND NOT "IsAccountAutoProvisionEnabled"
    THEN 1 ELSE 0 END
FROM "Providers" WHERE "Id" = :provider_id;
EXECUTE phase_b_provider_flow('phase-b-provider-policy', 7);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_provider_flow('phase-b-provider-policy', 8);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_provider_flow('phase-b-provider-policy', 9);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;

INSERT INTO "ProviderMediaConnections"
    ("ProfileId", "ProviderId", "ExternalAccountId", "ConsentedAt", "CredentialsStorageKey")
VALUES (:profile_id, :provider_id, 'external-provider-profile-1', now(), 'opaque-protected-fixture');
EXECUTE phase_b_provider_profile_media(:owner_id, :profile_id, 'phase-b-provider-policy');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;

UPDATE "Providers" SET "IsAccountLoginEnabled" = TRUE WHERE "Id" = :provider_id;
EXECUTE phase_b_provider_flow('phase-b-provider-policy', 7);
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
EXECUTE phase_b_provider_flow('phase-b-provider-policy', 8);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_provider_flow('phase-b-provider-policy', 9);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_provider_profile_media(:owner_id, :profile_id, 'phase-b-provider-policy');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
SELECT 1 / CASE WHEN NOT "IsAccountAutoProvisionEnabled" THEN 1 ELSE 0 END
FROM "Providers" WHERE "Id" = :provider_id;

UPDATE "Providers"
SET "IsAccountLinkEnabled" = TRUE, "IsMediaConnectionEnabled" = TRUE
WHERE "Id" = :provider_id;
EXECUTE phase_b_provider_flow('phase-b-provider-policy', 8);
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
EXECUTE phase_b_provider_flow('phase-b-provider-policy', 9);
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
EXECUTE phase_b_provider_flow('phase-b-provider-policy', 6);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;

EXECUTE phase_b_provider_profile_media(:owner_id, :profile_id, 'phase-b-provider-policy') \gset
SELECT 1 / CASE WHEN :ConnectionInternalId > 0
    AND :'ExternalAccountId' = 'external-provider-profile-1'
    AND :'IsWatchlistSyncEnabled' = 'f'
    AND :'IsProgressSyncEnabled' = 'f' THEN 1 ELSE 0 END;
EXECUTE phase_b_provider_profile_media(:outsider_id, :profile_id, 'phase-b-provider-policy');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId")
VALUES (:outsider_id, :profile_id);
EXECUTE phase_b_provider_profile_media(:outsider_id, :profile_id, 'phase-b-provider-policy');
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
DELETE FROM "AccountProfiles"
WHERE "AccountId" = :outsider_id AND "ProfileId" = :profile_id;
EXECUTE phase_b_provider_profile_media(:outsider_id, :profile_id, 'phase-b-provider-policy');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;

UPDATE "Accounts" SET "IsEnabled" = FALSE WHERE "Id" = :owner_id;
EXECUTE phase_b_provider_profile_media(:owner_id, :profile_id, 'phase-b-provider-policy');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
UPDATE "Accounts" SET "IsEnabled" = TRUE WHERE "Id" = :owner_id;
UPDATE "ProviderMediaConnections" SET "IsEnabled" = FALSE WHERE "ProfileId" = :profile_id;
EXECUTE phase_b_provider_profile_media(:owner_id, :profile_id, 'phase-b-provider-policy');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
UPDATE "ProviderMediaConnections" SET "IsEnabled" = TRUE WHERE "ProfileId" = :profile_id;
UPDATE "Providers" SET "IsEnabled" = FALSE WHERE "Id" = :provider_id;
EXECUTE phase_b_provider_flow('phase-b-provider-policy', 9);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_provider_profile_media(:owner_id, :profile_id, 'phase-b-provider-policy');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;

ROLLBACK;
