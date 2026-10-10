BEGIN;
SET LOCAL statement_timeout = '20s';

INSERT INTO "UiLocales" ("Locale", "Name")
VALUES ('phase-b-context', 'Context test')
RETURNING "Id" AS locale_id \gset
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('phase-b-context-a@example.invalid', 'Context A', 2)
RETURNING "Id" AS account_a \gset
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('phase-b-context-b@example.invalid', 'Context B', 2)
RETURNING "Id" AS account_b \gset
INSERT INTO "Profiles" ("OwnerAccountId", "DisplayName", "UiLocaleId")
VALUES (:account_a, 'Context A', :locale_id)
RETURNING "Id" AS profile_a \gset
INSERT INTO "Profiles" ("OwnerAccountId", "DisplayName", "UiLocaleId")
VALUES (:account_b, 'Context B', :locale_id)
RETURNING "Id" AS profile_b \gset
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId")
VALUES (:account_a, :profile_a), (:account_b, :profile_b);
INSERT INTO "LearningUnits" ("LearningUnitKindTypeId")
VALUES (1)
RETURNING "Id" AS unit_id, "PublicId" AS unit_public_id \gset
INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle")
VALUES (3, 'Context Book A')
RETURNING "Id" AS book_a \gset
INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle")
VALUES (3, 'Context Book B')
RETURNING "Id" AS book_b \gset
INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle")
VALUES (2, 'Context Series')
RETURNING "Id" AS series_id \gset
INSERT INTO "WorkChapters" ("WorkId", "OrderIndex")
VALUES (:book_a, 0)
RETURNING "Id" AS chapter_id \gset
INSERT INTO "WorkSeasons" ("WorkId", "SeasonNumber")
VALUES (:series_id, 1)
RETURNING "Id" AS season_id \gset
INSERT INTO "WorkEpisodes" ("WorkId", "WorkSeasonId", "OrderIndex")
VALUES (:series_id, :season_id, 0)
RETURNING "Id" AS episode_id \gset

INSERT INTO "LearningContexts" (
    "ProfileId", "LearningUnitId", "WorkId", "WorkChapterId", "LanguageTag", "SourceText")
VALUES
    (:profile_a, :unit_id, :book_a, :chapter_id, 'ja', 'Personal A'),
    (:profile_b, :unit_id, :book_a, :chapter_id, 'ja', 'Personal B');
INSERT INTO "LearningContexts" (
    "ProfileId", "LearningUnitId", "WorkId", "WorkChapterId", "PositionKey", "LanguageTag", "SourceText")
SELECT
    :profile_a,
    :unit_id,
    :book_a,
    :chapter_id,
    'paragraph:' || sequence."Number"::text,
    'ja',
    'Paragraph ' || sequence."Number"::text
FROM generate_series(1, 1000) AS sequence("Number");
INSERT INTO "LearningContexts" (
    "ProfileId", "LearningUnitId", "WorkId", "WorkEpisodeId", "PositionKey", "LanguageTag", "SourceText")
VALUES (:profile_a, :unit_id, :series_id, :episode_id, 'cue:1000', 'ja', 'Episode cue');

DO $context_integrity$
DECLARE
    personal_context "LearningContexts"%ROWTYPE;
    chapter_context "LearningContexts"%ROWTYPE;
    other_work_id bigint;
    course_id bigint;
    rejected_constraint text;
BEGIN
    SELECT context.* INTO STRICT personal_context
    FROM "LearningContexts" AS context
    WHERE context."SourceText" = 'Personal A';
    SELECT context.* INTO STRICT chapter_context
    FROM "LearningContexts" AS context
    WHERE context."SourceText" = 'Personal B';
    SELECT "Id" INTO STRICT other_work_id FROM "Works" WHERE "CanonicalTitle" = 'Context Book B';
    INSERT INTO "LearningCourses" ("ProfileId", "SourceLanguage", "TargetLanguage", "DisplayName")
    VALUES (personal_context."ProfileId", 'ja', 'de', 'Context course')
    RETURNING "Id" INTO course_id;
    IF NOT EXISTS (
        SELECT 1 FROM "LearningCourses" AS course
        WHERE course."Id" = course_id AND course."RecognitionEnabled" AND course."SentencePracticeEnabled"
            AND NOT course."ProductionEnabled" AND NOT course."ListeningEnabled" AND NOT course."WritingEnabled"
    ) THEN
        RAISE EXCEPTION 'Course mode defaults were lost';
    END IF;
    UPDATE "LearningCourses"
    SET "RecognitionEnabled" = false, "ProductionEnabled" = true, "ListeningEnabled" = true,
        "WritingEnabled" = true, "SentencePracticeEnabled" = false, "UpdatedAt" = now()
    WHERE "Id" = course_id;
    IF NOT EXISTS (
        SELECT 1 FROM "LearningCourses" AS course
        WHERE course."Id" = course_id AND NOT course."RecognitionEnabled" AND NOT course."SentencePracticeEnabled"
            AND course."ProductionEnabled" AND course."ListeningEnabled" AND course."WritingEnabled"
    ) THEN
        RAISE EXCEPTION 'Independent course modes did not persist';
    END IF;
    INSERT INTO "LearningCards" (
        "LearningCourseId", "ProfileId", "LearningUnitId", "LearningCardModeTypeId", "LearningCardStateTypeId")
    VALUES (course_id, personal_context."ProfileId", personal_context."LearningUnitId", 1, 3);
    BEGIN
        UPDATE "LearningCards" SET "ProfileId" = chapter_context."ProfileId" WHERE "LearningCourseId" = course_id;
        RAISE EXCEPTION 'Card from another profile course was accepted';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'FK_LearningCards_LearningCourses' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO "LearningContexts" (
            "ProfileId", "LearningUnitId", "WorkId", "WorkChapterId", "LanguageTag", "SourceText")
        VALUES (personal_context."ProfileId", personal_context."LearningUnitId", personal_context."WorkId",
            personal_context."WorkChapterId", 'ja', 'Duplicate NULL position');
        RAISE EXCEPTION 'Duplicate personal NULL position was accepted';
    EXCEPTION WHEN unique_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'UX_LearningContexts_Profile_Unit_Content_Position' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "LearningContexts" SET "WorkId" = other_work_id WHERE "Id" = personal_context."Id";
        RAISE EXCEPTION 'Chapter from another Work was accepted';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'FK_LearningContexts_WorkChapters' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "LearningContexts" SET "WorkId" = other_work_id WHERE "SourceText" = 'Episode cue';
        RAISE EXCEPTION 'Episode from another Work was accepted';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'FK_LearningContexts_WorkEpisodes' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "LearningContexts" SET "ProfileId" = 9223372036854775807 WHERE "Id" = personal_context."Id";
        RAISE EXCEPTION 'Missing profile was accepted';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'FK_LearningContexts_Profiles' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "LearningContexts" SET "WorkChapterId" = NULL WHERE "Id" = personal_context."Id";
        RAISE EXCEPTION 'Context without content was accepted';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'CK_LearningContexts_Content' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "LearningContexts"
        SET "WorkChapterId" = chapter_context."WorkChapterId"
        WHERE "SourceText" = 'Episode cue';
        RAISE EXCEPTION 'Context with both episode and chapter was accepted';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'CK_LearningContexts_Content' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "LearningContexts" SET "PositionKey" = '   ' WHERE "Id" = personal_context."Id";
        RAISE EXCEPTION 'Blank position was accepted';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'CK_LearningContexts_Position' THEN RAISE; END IF;
    END;
END $context_integrity$;

\i /tmp/phase_b_contexts_prepared.sql
CREATE TEMP TABLE context_first_page AS EXECUTE phase_b_contexts(:account_a, :profile_a, :'unit_public_id', 25, 0);
CREATE TEMP TABLE context_second_page AS EXECUTE phase_b_contexts(:account_a, :profile_a, :'unit_public_id', 25, 25);
CREATE TEMP TABLE context_last_page AS EXECUTE phase_b_contexts(:account_a, :profile_a, :'unit_public_id', 25, 1000);
CREATE TEMP TABLE context_empty_page AS EXECUTE phase_b_contexts(:account_a, :profile_a, :'unit_public_id', 25, 1002);
CREATE TEMP TABLE context_other_profile AS EXECUTE phase_b_contexts(:account_a, :profile_b, :'unit_public_id', 25, 0);
CREATE TEMP TABLE context_own_b AS EXECUTE phase_b_contexts(:account_b, :profile_b, :'unit_public_id', 25, 0);
CREATE TEMP TABLE context_unknown_actor AS EXECUTE phase_b_contexts(9223372036854775807, :profile_a, :'unit_public_id', 25, 0);
CREATE TEMP TABLE context_unknown_unit AS EXECUTE phase_b_contexts(:account_a, :profile_a, 'ffffffff-ffff-ffff-ffff-ffffffffffff', 25, 0);
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (:account_b, :profile_a);
CREATE TEMP TABLE context_shared_page AS EXECUTE phase_b_contexts(:account_b, :profile_a, :'unit_public_id', 25, 0);
DELETE FROM "AccountProfiles" WHERE "AccountId" = :account_b AND "ProfileId" = :profile_a;
CREATE TEMP TABLE context_revoked_page AS EXECUTE phase_b_contexts(:account_b, :profile_a, :'unit_public_id', 25, 0);
UPDATE "Accounts" SET "IsEnabled" = false WHERE "Id" = :account_a;
CREATE TEMP TABLE context_disabled_page AS EXECUTE phase_b_contexts(:account_a, :profile_a, :'unit_public_id', 25, 0);
UPDATE "Accounts" SET "IsEnabled" = true WHERE "Id" = :account_a;

DO $context_read$
BEGIN
    IF (SELECT count(*) FROM context_first_page) <> 25
        OR (SELECT count(*) FROM context_second_page) <> 25
        OR (SELECT count(*) FROM context_last_page) <> 2
        OR (SELECT count(*) FROM context_shared_page) <> 25
        OR (SELECT count(*) FROM context_own_b) <> 1
        OR EXISTS (SELECT 1 FROM context_empty_page)
        OR EXISTS (SELECT 1 FROM context_other_profile)
        OR EXISTS (SELECT 1 FROM context_unknown_actor)
        OR EXISTS (SELECT 1 FROM context_unknown_unit)
        OR EXISTS (SELECT 1 FROM context_revoked_page)
        OR EXISTS (SELECT 1 FROM context_disabled_page) THEN
        RAISE EXCEPTION 'Context paging or profile authorization failed';
    END IF;
    IF EXISTS (
        SELECT 1
        FROM context_first_page AS first_page
        JOIN context_second_page AS second_page ON second_page."Id" = first_page."Id"
    ) OR NOT EXISTS (SELECT 1 FROM context_own_b WHERE "SourceText" = 'Personal B') THEN
        RAISE EXCEPTION 'Context unique ordering or personal isolation failed';
    END IF;
    IF EXISTS (
        SELECT 1
        FROM context_first_page AS result
        LEFT JOIN "LearningContexts" AS context ON context."PublicId" = result."Id"
        LEFT JOIN "LearningUnits" AS unit ON unit."PublicId" = result."LearningUnitId"
        LEFT JOIN "Works" AS work ON work."PublicId" = result."WorkId"
        WHERE context."Id" IS NULL OR unit."Id" IS NULL OR work."Id" IS NULL
            OR unit."Id" <> context."LearningUnitId" OR work."Id" <> context."WorkId"
    ) THEN
        RAISE EXCEPTION 'Context outward UUID projection failed';
    END IF;
    IF NOT EXISTS (
        SELECT 1
        FROM context_last_page AS result
        JOIN "WorkEpisodes" AS episode ON episode."PublicId" = result."WorkEpisodeId"
        WHERE result."SourceText" = 'Episode cue' AND result."WorkChapterId" IS NULL
    ) OR NOT EXISTS (
        SELECT 1
        FROM context_first_page AS result
        JOIN "WorkChapters" AS chapter ON chapter."PublicId" = result."WorkChapterId"
        WHERE result."SourceText" = 'Personal A' AND result."WorkEpisodeId" IS NULL
    ) THEN
        RAISE EXCEPTION 'Context typed public content reference failed';
    END IF;
END $context_read$;

ANALYZE "LearningContexts";
ANALYZE "LearningUnits";
EXPLAIN (ANALYZE, BUFFERS) EXECUTE phase_b_contexts(:account_a, :profile_a, :'unit_public_id', 25, 0);
ROLLBACK;
