BEGIN;
SET LOCAL statement_timeout = '20s';
DO $cyclic_lifecycle$
DECLARE
    locale_id bigint;
    account_id bigint;
    profile_id bigint;
    work_id bigint;
    progress_id bigint;
    position_type smallint;
    rejected_constraint text;
BEGIN
    INSERT INTO "UiLocales" ("Locale", "Name") VALUES ('phase-b-cyclic', 'Cyclic lifecycle') RETURNING "Id" INTO locale_id;
    INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
    VALUES ('phase-b-cyclic@example.invalid', 'Cyclic lifecycle', 1) RETURNING "Id" INTO account_id;
    INSERT INTO "Profiles" ("OwnerAccountId", "DisplayName", "UiLocaleId")
    VALUES (account_id, 'Cyclic lifecycle', locale_id) RETURNING "Id" INTO profile_id;
    INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (account_id, profile_id);
    INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (1, 'Cyclic lifecycle') RETURNING "Id" INTO work_id;
    SET CONSTRAINTS ALL IMMEDIATE;
    SET CONSTRAINTS ALL DEFERRED;

    FOR position_type IN 1..3
    LOOP
        INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "ProgressPositionTypeId")
        VALUES (profile_id, work_id, position_type) RETURNING "Id" INTO progress_id;
        IF position_type = 1 THEN
            INSERT INTO "TimeProgressPositions" ("MediaProgressId") VALUES (progress_id);
        ELSIF position_type = 2 THEN
            INSERT INTO "ReadingProgressPositions" ("MediaProgressId", "WorkId") VALUES (progress_id, work_id);
        ELSE
            INSERT INTO "GameProgressPositions" ("MediaProgressId") VALUES (progress_id);
        END IF;
        SET CONSTRAINTS ALL IMMEDIATE;
        SET CONSTRAINTS ALL DEFERRED;
        BEGIN
            IF position_type = 1 THEN
                DELETE FROM "TimeProgressPositions" WHERE "MediaProgressId" = progress_id;
            ELSIF position_type = 2 THEN
                DELETE FROM "ReadingProgressPositions" WHERE "MediaProgressId" = progress_id;
            ELSE
                DELETE FROM "GameProgressPositions" WHERE "MediaProgressId" = progress_id;
            END IF;
            SET CONSTRAINTS ALL IMMEDIATE;
            RAISE EXCEPTION 'Progress survived without its required position';
        EXCEPTION WHEN foreign_key_violation THEN
            GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
            IF rejected_constraint NOT IN ('FK_MediaProgress_TimeProgressPositions', 'FK_MediaProgress_ReadingProgressPositions', 'FK_MediaProgress_GameProgressPositions') THEN
                RAISE;
            END IF;
        END;
        SET CONSTRAINTS ALL DEFERRED;
        IF position_type = 1 THEN
            DELETE FROM "TimeProgressPositions" WHERE "MediaProgressId" = progress_id;
        ELSIF position_type = 2 THEN
            DELETE FROM "ReadingProgressPositions" WHERE "MediaProgressId" = progress_id;
        ELSE
            DELETE FROM "GameProgressPositions" WHERE "MediaProgressId" = progress_id;
        END IF;
        DELETE FROM "MediaProgress" WHERE "Id" = progress_id;
        SET CONSTRAINTS ALL IMMEDIATE;
        SET CONSTRAINTS ALL DEFERRED;
    END LOOP;

    BEGIN
        DELETE FROM "AccountProfiles" WHERE "AccountId" = account_id AND "ProfileId" = profile_id;
        SET CONSTRAINTS "FK_Profiles_OwnerAccountProfiles" IMMEDIATE;
        RAISE EXCEPTION 'Profile survived without owner membership';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'FK_Profiles_OwnerAccountProfiles' THEN RAISE; END IF;
    END;
    SET CONSTRAINTS ALL DEFERRED;
    DELETE FROM "AccountProfiles" WHERE "AccountId" = account_id AND "ProfileId" = profile_id;
    DELETE FROM "Profiles" WHERE "Id" = profile_id;
    DELETE FROM "Accounts" WHERE "Id" = account_id;
    DELETE FROM "UiLocales" WHERE "Id" = locale_id;
    DELETE FROM "Works" WHERE "Id" = work_id;
    SET CONSTRAINTS ALL IMMEDIATE;
    RAISE NOTICE 'Cyclic required relationships reject orphans and allow explicit transactional deletion';
END $cyclic_lifecycle$;
ROLLBACK;
