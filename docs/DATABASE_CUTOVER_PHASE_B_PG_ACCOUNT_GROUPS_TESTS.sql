BEGIN;
SET LOCAL statement_timeout = '20s';

INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES
    ('phase-b-groups-owner@example.invalid', 'Groups owner', 1),
    ('phase-b-groups-user@example.invalid', 'Groups user', 2),
    ('phase-b-groups-manager@example.invalid', 'Groups manager', 3);
SELECT "Id" AS owner_id FROM "Accounts" WHERE "Email" = 'phase-b-groups-owner@example.invalid' \gset
SELECT "Id" AS user_id FROM "Accounts" WHERE "Email" = 'phase-b-groups-user@example.invalid' \gset
SELECT "Id" AS manager_id FROM "Accounts" WHERE "Email" = 'phase-b-groups-manager@example.invalid' \gset

INSERT INTO "AccountGroups" ("Name")
SELECT 'Group ' || lpad(sequence."Number"::text, 4, '0')
FROM generate_series(1, 1000) AS sequence("Number");
SELECT "Id" AS first_group_id FROM "AccountGroups" WHERE "Name" = 'Group 0001' \gset
INSERT INTO "AccountGroupAccounts" ("AccountGroupId", "AccountId")
VALUES (:first_group_id, :user_id);
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
SELECT
    'phase-b-groups-load-' || sequence."Number"::text || '@example.invalid',
    'Groups load member ' || sequence."Number"::text,
    2
FROM generate_series(1, 5) AS sequence("Number");
INSERT INTO "AccountGroupAccounts" ("AccountGroupId", "AccountId")
SELECT
    account_group."Id",
    account."Id"
FROM "AccountGroups" AS account_group
CROSS JOIN "Accounts" AS account
WHERE account."Email" LIKE 'phase-b-groups-load-%@example.invalid';

DO $group_integrity$
DECLARE
    group_id bigint;
    account_id bigint;
    invalid_name text;
    rejected_constraint text;
BEGIN
    SELECT "Id" INTO group_id FROM "AccountGroups" WHERE "Name" = 'Group 0001';
    SELECT "Id" INTO account_id FROM "Accounts" WHERE "Email" = 'phase-b-groups-user@example.invalid';

    FOREACH invalid_name IN ARRAY ARRAY['', ' ', ' padded', 'padded ', repeat('x', 81)]
    LOOP
        BEGIN
            INSERT INTO "AccountGroups" ("Name") VALUES (invalid_name);
            RAISE EXCEPTION 'AccountGroups accepted an invalid name';
        EXCEPTION WHEN check_violation THEN
            GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
            IF rejected_constraint <> 'CK_AccountGroups_Name' THEN
                RAISE;
            END IF;
        END;
    END LOOP;

    BEGIN
        INSERT INTO "AccountGroups" ("Name") VALUES ('gRoUp 0001');
        RAISE EXCEPTION 'AccountGroups accepted a case-insensitive duplicate';
    EXCEPTION WHEN unique_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'UX_AccountGroups_Name' THEN
            RAISE;
        END IF;
    END;

    BEGIN
        INSERT INTO "AccountGroupAccounts" ("AccountGroupId", "AccountId") VALUES (group_id, account_id);
        RAISE EXCEPTION 'AccountGroupAccounts accepted a duplicate membership';
    EXCEPTION WHEN unique_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'PK_AccountGroupAccounts' THEN
            RAISE;
        END IF;
    END;

    BEGIN
        INSERT INTO "AccountGroupAccounts" ("AccountGroupId", "AccountId") VALUES (9223372036854775807, account_id);
        RAISE EXCEPTION 'AccountGroupAccounts accepted a missing group';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'FK_AccountGroupAccounts_AccountGroups' THEN
            RAISE;
        END IF;
    END;

    BEGIN
        INSERT INTO "AccountGroupAccounts" ("AccountGroupId", "AccountId") VALUES (group_id, 9223372036854775807);
        RAISE EXCEPTION 'AccountGroupAccounts accepted a missing account';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'FK_AccountGroupAccounts_Accounts' THEN
            RAISE;
        END IF;
    END;

    BEGIN
        DELETE FROM "AccountGroups" WHERE "Id" = group_id;
        RAISE EXCEPTION 'Group deletion silently removed membership';
    EXCEPTION WHEN restrict_violation OR foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'FK_AccountGroupAccounts_AccountGroups' THEN
            RAISE;
        END IF;
    END;

    BEGIN
        DELETE FROM "Accounts" WHERE "Id" = account_id;
        RAISE EXCEPTION 'Account deletion silently removed group membership';
    EXCEPTION WHEN restrict_violation OR foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'FK_AccountGroupAccounts_Accounts' THEN
            RAISE;
        END IF;
    END;

    BEGIN
        DELETE FROM "AccountGroupAccounts" WHERE "AccountId" = account_id;
        INSERT INTO "AccountGroups" ("Name") VALUES ('Group 0001');
        RAISE EXCEPTION 'Expected second mutation to fail';
    EXCEPTION WHEN unique_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'UX_AccountGroups_Name' THEN
            RAISE;
        END IF;
    END;
    IF NOT EXISTS (
        SELECT 1
        FROM "AccountGroupAccounts" AS member
        WHERE member."AccountGroupId" = group_id AND member."AccountId" = account_id
    ) THEN
        RAISE EXCEPTION 'Failed second mutation did not restore membership';
    END IF;
    IF (SELECT "AccountRoleTypeId" FROM "Accounts" WHERE "Id" = account_id) <> 2 THEN
        RAISE EXCEPTION 'Group membership changed the account role';
    END IF;
END $group_integrity$;

\i /tmp/phase_b_groups_prepared.sql
CREATE TEMP TABLE group_first_page AS EXECUTE phase_b_groups(:owner_id, 25, 0);
CREATE TEMP TABLE group_second_page AS EXECUTE phase_b_groups(:owner_id, 25, 25);
CREATE TEMP TABLE group_last_page AS EXECUTE phase_b_groups(:owner_id, 25, 975);
CREATE TEMP TABLE group_empty_page AS EXECUTE phase_b_groups(:owner_id, 25, 1000);
CREATE TEMP TABLE group_user_page AS EXECUTE phase_b_groups(:user_id, 25, 0);
CREATE TEMP TABLE group_manager_page AS EXECUTE phase_b_groups(:manager_id, 25, 0);
CREATE TEMP TABLE group_unknown_actor_page AS EXECUTE phase_b_groups(9223372036854775807, 25, 0);
UPDATE "Accounts" SET "IsEnabled" = FALSE WHERE "Id" = :owner_id;
CREATE TEMP TABLE group_disabled_page AS EXECUTE phase_b_groups(:owner_id, 25, 0);
UPDATE "Accounts" SET "IsEnabled" = TRUE WHERE "Id" = :owner_id;

DO $group_read$
BEGIN
    IF (SELECT count(*) FROM group_first_page) <> 25
        OR (SELECT count(*) FROM group_second_page) <> 25
        OR (SELECT count(*) FROM group_last_page) <> 25
        OR EXISTS (SELECT 1 FROM group_empty_page)
        OR EXISTS (SELECT 1 FROM group_user_page)
        OR EXISTS (SELECT 1 FROM group_manager_page)
        OR EXISTS (SELECT 1 FROM group_unknown_actor_page)
        OR EXISTS (SELECT 1 FROM group_disabled_page) THEN
        RAISE EXCEPTION 'Groups paging or owner-only scope failed';
    END IF;
    IF (SELECT min("Name") FROM group_first_page) <> 'Group 0001'
        OR (SELECT min("Name") FROM group_second_page) <> 'Group 0026'
        OR (SELECT min("Name") FROM group_last_page) <> 'Group 0976' THEN
        RAISE EXCEPTION 'Groups paging order failed';
    END IF;
    IF NOT EXISTS (
        SELECT 1
        FROM group_first_page AS result
        INNER JOIN "AccountGroups" AS account_group ON account_group."PublicId" = result."Id"
        WHERE account_group."Name" = 'Group 0001' AND result."MemberCount" = 6
    ) OR EXISTS (SELECT 1 FROM group_first_page WHERE "Name" <> 'Group 0001' AND "MemberCount" <> 5) THEN
        RAISE EXCEPTION 'Groups public UUID or bounded membership aggregate failed';
    END IF;
END $group_read$;

ANALYZE "AccountGroups";
ANALYZE "AccountGroupAccounts";
EXPLAIN (ANALYZE, BUFFERS) EXECUTE phase_b_groups(:owner_id, 25, 0);
ROLLBACK;
