\set ON_ERROR_STOP on
BEGIN;
SET LOCAL statement_timeout = '20s';
\o /dev/null
\i /tmp/phase_b_auth_session_prepared.sql
\i /tmp/phase_b_auth_sessions_prepared.sql
\i /tmp/phase_b_auth_rotate_prepared.sql
\i /tmp/phase_b_auth_challenge_prepared.sql
\i /tmp/phase_b_auth_recovery_prepared.sql
\i /tmp/phase_b_auth_totp_prepared.sql
\i /tmp/phase_b_auth_invalidate_prepared.sql
SELECT now() AS test_now, now() + INTERVAL '1 day' AS test_expiry \gset
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('auth-life@example.invalid', 'Auth lifecycle', 2) RETURNING "Id" AS actor_id \gset
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('auth-other@example.invalid', 'Auth other', 1) RETURNING "Id" AS other_id \gset
INSERT INTO "UiLocales" ("Locale", "Name") VALUES ('phase-b-auth', 'Auth') RETURNING "Id" AS locale_id \gset
INSERT INTO "Profiles" ("OwnerAccountId", "DisplayName", "UiLocaleId")
VALUES (:actor_id, 'Auth profile', :locale_id) RETURNING "Id" AS profile_id \gset
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (:actor_id, :profile_id);
INSERT INTO "AccountSessions" ("AccountId", "ActiveProfileId", "TokenHash", "ExpiresAt")
VALUES (:actor_id, :profile_id, decode(repeat('31', 32), 'hex'), :'test_expiry');
EXECUTE phase_b_auth_session(decode(repeat('31', 32), 'hex'), :'test_now');
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
EXECUTE phase_b_auth_rotate(:other_id, decode(repeat('31', 32), 'hex'), decode(repeat('32', 32), 'hex'), 1, :'test_now', :'test_expiry');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_auth_rotate(:actor_id, decode(repeat('31', 32), 'hex'), decode(repeat('32', 32), 'hex'), 1, :'test_now', :'test_expiry');
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
EXECUTE phase_b_auth_rotate(:actor_id, decode(repeat('31', 32), 'hex'), decode(repeat('33', 32), 'hex'), 1, :'test_now', :'test_expiry');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_auth_session(decode(repeat('31', 32), 'hex'), :'test_now');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_auth_session(decode(repeat('32', 32), 'hex'), :'test_now');
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
EXECUTE phase_b_auth_session(decode(repeat('32', 32), 'hex'), :'test_expiry');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;

INSERT INTO "AccountSessions" ("AccountId", "TokenHash", "ExpiresAt", "CreatedAt")
SELECT :actor_id, sha256(number::text::bytea), :'test_expiry', :'test_now'
FROM generate_series(1, 1000) AS number;
INSERT INTO "AccountSessions" ("AccountId", "TokenHash", "ExpiresAt")
VALUES (:other_id, decode(repeat('34', 32), 'hex'), :'test_expiry');
EXECUTE phase_b_auth_sessions(:actor_id, 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 25 THEN 1 ELSE 0 END;
EXECUTE phase_b_auth_sessions(:actor_id, 100, 1000);
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
EXECUTE phase_b_auth_sessions(:actor_id, 101, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_auth_sessions(:actor_id, 25, 100001);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;

INSERT INTO "AccountAuthChallenges" ("AccountId", "CredentialRevision", "AuthChallengePurposeTypeId", "TokenHash", "ExpiresAt")
VALUES (:actor_id, 1, 2, decode(repeat('41', 32), 'hex'), :'test_expiry') RETURNING "PublicId" AS reset_id \gset
EXECUTE phase_b_auth_challenge(:'reset_id', decode(repeat('41', 32), 'hex'), 1, :actor_id, :'test_now', NULL, NULL);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_auth_challenge(:'reset_id', decode(repeat('42', 32), 'hex'), 2, :actor_id, :'test_now', NULL, NULL);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_auth_challenge(:'reset_id', decode(repeat('41', 32), 'hex'), 2, :other_id, :'test_now', NULL, NULL);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
SAVEPOINT reset_effect;
EXECUTE phase_b_auth_challenge(:'reset_id', decode(repeat('41', 32), 'hex'), 2, :actor_id, :'test_now', NULL, NULL);
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
ROLLBACK TO SAVEPOINT reset_effect;
EXECUTE phase_b_auth_challenge(:'reset_id', decode(repeat('41', 32), 'hex'), 2, :actor_id, :'test_now', NULL, NULL);
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
EXECUTE phase_b_auth_challenge(:'reset_id', decode(repeat('41', 32), 'hex'), 2, :actor_id, :'test_now', NULL, NULL);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;

INSERT INTO "AccountRecoveryCodes" ("AccountId", "CodeHash")
VALUES (:actor_id, decode(repeat('51', 32), 'hex'));
EXECUTE phase_b_auth_recovery(:other_id, decode(repeat('51', 32), 'hex'), :'test_now');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_auth_recovery(:actor_id, decode(repeat('51', 32), 'hex'), :'test_now');
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
EXECUTE phase_b_auth_recovery(:actor_id, decode(repeat('51', 32), 'hex'), :'test_now');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
INSERT INTO "AccountTotpFactors" ("AccountId", "EncryptedSecret", "ConfirmedAt")
VALUES (:actor_id, decode('00', 'hex'), :'test_now') RETURNING "Id" AS factor_id \gset
EXECUTE phase_b_auth_totp(:other_id, :factor_id, 100);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_auth_totp(:actor_id, :factor_id, 100);
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
EXECUTE phase_b_auth_totp(:actor_id, :factor_id, 100);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_auth_totp(:actor_id, :factor_id, 99);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
UPDATE "AccountTotpFactors" SET "RevokedAt" = :'test_now' WHERE "Id" = :factor_id;
EXECUTE phase_b_auth_totp(:actor_id, :factor_id, 101);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;

INSERT INTO "Providers" ("Key") VALUES ('phase-b-auth-plex') RETURNING "Id" AS provider_id \gset
INSERT INTO "AccountAuthChallenges" ("AccountId", "CredentialRevision", "AuthChallengePurposeTypeId", "TokenHash", "ExpiresAt")
VALUES (:actor_id, 1, 9, decode(repeat('61', 32), 'hex'), :'test_expiry') RETURNING "Id" AS challenge_id, "PublicId" AS media_id \gset
INSERT INTO "AccountExternalAuthFlows" ("AccountAuthChallengeId", "AuthChallengePurposeTypeId", "ProviderId", "StartedProfileId", "ExternalPinId", "ClientIdentifier")
VALUES (:challenge_id, 9, :provider_id, :profile_id, 123, 'phase-b-fixture');
EXECUTE phase_b_auth_challenge(:'media_id', decode(repeat('61', 32), 'hex'), 9, :actor_id, :'test_now', :provider_id, :profile_id);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
UPDATE "AccountExternalAuthFlows" SET "VerifiedExternalAccountId" = 'verified-fixture' WHERE "AccountAuthChallengeId" = :challenge_id;
EXECUTE phase_b_auth_challenge(:'media_id', decode(repeat('61', 32), 'hex'), 7, :actor_id, :'test_now', :provider_id, :profile_id);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_auth_challenge(:'media_id', decode(repeat('61', 32), 'hex'), 9, :actor_id, :'test_now', :provider_id, NULL);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_auth_challenge(:'media_id', decode(repeat('61', 32), 'hex'), 9, :actor_id, :'test_now', :provider_id, :profile_id);
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
SELECT 1 / CASE WHEN NOT EXISTS (SELECT 1 FROM "AccountExternalLogins" WHERE "AccountId" = :actor_id) THEN 1 ELSE 0 END;
SELECT 1 / CASE WHEN NOT EXISTS (SELECT 1 FROM "ProviderMediaConnections" WHERE "ProfileId" = :profile_id) THEN 1 ELSE 0 END;

ANALYZE "AccountSessions";
\o
EXPLAIN (ANALYZE, BUFFERS) EXECUTE phase_b_auth_session(decode(repeat('32', 32), 'hex'), :'test_now');
\o /dev/null
INSERT INTO "AccountAuthChallenges" ("AccountId", "CredentialRevision", "AuthChallengePurposeTypeId", "TokenHash", "ExpiresAt")
VALUES (:actor_id, 1, 9, decode(repeat('62', 32), 'hex'), :'test_expiry') RETURNING "Id" AS challenge_id, "PublicId" AS transfer_media_id \gset
INSERT INTO "AccountExternalAuthFlows" ("AccountAuthChallengeId", "AuthChallengePurposeTypeId", "ProviderId", "StartedProfileId", "ExternalPinId", "ClientIdentifier", "VerifiedExternalAccountId")
VALUES (:challenge_id, 9, :provider_id, :profile_id, 124, 'phase-b-fixture', 'verified-fixture');
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (:other_id, :profile_id);
UPDATE "Profiles" SET "OwnerAccountId" = :other_id WHERE "Id" = :profile_id;
UPDATE "AccountSessions" SET "ActiveProfileId" = NULL WHERE "AccountId" = :actor_id AND "ActiveProfileId" = :profile_id;
DELETE FROM "AccountProfiles" WHERE "AccountId" = :actor_id AND "ProfileId" = :profile_id;
SET CONSTRAINTS ALL IMMEDIATE;
EXECUTE phase_b_auth_challenge(:'transfer_media_id', decode(repeat('62', 32), 'hex'), 9, :actor_id, :'test_now', :provider_id, :profile_id);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;

EXECUTE phase_b_auth_invalidate(:actor_id, 1, :'test_now');
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
EXECUTE phase_b_auth_invalidate(:actor_id, 1, :'test_now');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
SELECT 1 / CASE WHEN NOT EXISTS (SELECT 1 FROM "AccountSessions" WHERE "AccountId" = :actor_id AND "RevokedAt" IS NULL) THEN 1 ELSE 0 END;
EXECUTE phase_b_auth_session(decode(repeat('32', 32), 'hex'), :'test_now');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_auth_rotate(:actor_id, decode(repeat('32', 32), 'hex'), decode(repeat('33', 32), 'hex'), 2, :'test_now', :'test_expiry');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
UPDATE "Accounts" SET "IsEnabled" = FALSE WHERE "Id" = :actor_id;
EXECUTE phase_b_auth_sessions(:actor_id, 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
UPDATE "Accounts" SET "IsEnabled" = TRUE WHERE "Id" = :actor_id;

DO $auth_constraints$
DECLARE
    actor_id bigint;
    challenge_id bigint;
    provider_id bigint;
    rejected text;
BEGIN
    SELECT "Id" INTO actor_id FROM "Accounts" WHERE "Email" = 'auth-life@example.invalid';
    SELECT "Id" INTO provider_id FROM "Providers" WHERE "Key" = 'phase-b-auth-plex';
    BEGIN
        INSERT INTO "AccountAuthChallenges" ("AuthChallengePurposeTypeId", "TokenHash", "ExpiresAt")
        VALUES (2, sha256('missing-account'::bytea), now() + INTERVAL '1 hour');
        RAISE EXCEPTION 'Accountless reset was accepted';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected = CONSTRAINT_NAME;
        IF rejected <> 'CK_AccountAuthChallenges_Account' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO "AccountAuthChallenges" ("AccountId", "AuthChallengePurposeTypeId", "TokenHash", "ExpiresAt")
        VALUES (actor_id, 2, sha256('missing-revision'::bytea), now() + INTERVAL '1 hour');
        RAISE EXCEPTION 'Null revision was accepted';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected = CONSTRAINT_NAME;
        IF rejected <> 'CK_AccountAuthChallenges_Account' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO "AccountSessions" ("AccountId", "TokenHash", "ExpiresAt")
        VALUES (actor_id, decode('00', 'hex'), now() + INTERVAL '1 hour');
        RAISE EXCEPTION 'Weak session digest was accepted';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected = CONSTRAINT_NAME;
        IF rejected <> 'CK_AccountSessions_Hash' THEN RAISE; END IF;
    END;
    INSERT INTO "AccountAuthChallenges" ("AccountId", "CredentialRevision", "AuthChallengePurposeTypeId", "TokenHash", "ExpiresAt")
    VALUES (actor_id, 2, 8, sha256('bad-return-path'::bytea), now() + INTERVAL '1 hour') RETURNING "Id" INTO challenge_id;
    BEGIN
        INSERT INTO "AccountExternalAuthFlows" ("AccountAuthChallengeId", "AuthChallengePurposeTypeId", "ProviderId", "ExternalPinId", "ClientIdentifier", "ReturnPath")
        VALUES (challenge_id, 8, provider_id, 321, 'fixture', '//evil.invalid');
        RAISE EXCEPTION 'Network-path return URL was accepted';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected = CONSTRAINT_NAME;
        IF rejected <> 'CK_AccountExternalAuthFlows_ReturnPath' THEN RAISE; END IF;
    END;
END $auth_constraints$;
ANALYZE "AccountSessions";
\o
EXPLAIN (ANALYZE, BUFFERS) EXECUTE phase_b_auth_sessions(:actor_id, 25, 0);
EXPLAIN (ANALYZE, BUFFERS) EXECUTE phase_b_auth_session(decode(repeat('32', 32), 'hex'), :'test_now');
ROLLBACK;
