-- DESTRUCTIVE TEST FIXTURES: ONLY run on fresh, disposable Phase-B scratch PostgreSQL.
-- DO NOT execute against dev/production. All inserts roll back after test.
-- The DDL 01..10 and seed proposals must be installed first.
-- These are real negative FK/permission-scope rejection tests, not just catalog checks.
BEGIN;
DO $test$
DECLARE
    account_id bigint;
    locale_id bigint;
    profile_id bigint;
    blueprint_one uuid;
    blueprint_two uuid;
    level_one uuid;
    level_two uuid;
    chapter_one uuid;
    chapter_two uuid;
    lesson_one uuid;
    lesson_two uuid;
    exercise_one uuid;
    exercise_two uuid;
    shared_one uuid;
    enrollment_one uuid;
    progress_id bigint;
    work_id bigint;
BEGIN
    INSERT INTO "UiLocales" ("Locale","Name") VALUES ('en','English')
    RETURNING "Id" INTO locale_id;
    INSERT INTO "Accounts" ("Email","DisplayName","AccountRoleTypeId")
    VALUES ('phase-b-ci@example.invalid','Phase B Test',1)
    RETURNING "Id" INTO account_id;
    INSERT INTO "Profiles" ("OwnerAccountId","DisplayName","UiLocaleId")
    VALUES (account_id,'Phase B Profile',locale_id)
    RETURNING "Id" INTO profile_id;
    INSERT INTO "AccountProfiles" ("AccountId","ProfileId")
    VALUES (account_id,profile_id);

    INSERT INTO "CurriculumBlueprints" ("Key","Version")
    VALUES ('ci-blueprint-one',1) RETURNING "Id" INTO blueprint_one;
    INSERT INTO "CurriculumBlueprints" ("Key","Version")
    VALUES ('ci-blueprint-two',1) RETURNING "Id" INTO blueprint_two;
    INSERT INTO "CurriculumLevels" ("CurriculumBlueprintId","Ordinal")
    VALUES (blueprint_one,1) RETURNING "Id" INTO level_one;
    INSERT INTO "CurriculumLevels" ("CurriculumBlueprintId","Ordinal")
    VALUES (blueprint_two,1) RETURNING "Id" INTO level_two;
    INSERT INTO "CurriculumChapters" ("CurriculumLevelId","CurriculumBlueprintId","Ordinal")
    VALUES (level_one,blueprint_one,1) RETURNING "Id" INTO chapter_one;
    INSERT INTO "CurriculumChapters" ("CurriculumLevelId","CurriculumBlueprintId","Ordinal")
    VALUES (level_two,blueprint_two,1) RETURNING "Id" INTO chapter_two;
    INSERT INTO "CurriculumLessons" ("CurriculumChapterId","CurriculumBlueprintId","Ordinal")
    VALUES (chapter_one,blueprint_one,1) RETURNING "Id" INTO lesson_one;
    INSERT INTO "CurriculumLessons" ("CurriculumChapterId","CurriculumBlueprintId","Ordinal")
    VALUES (chapter_two,blueprint_two,1) RETURNING "Id" INTO lesson_two;

    INSERT INTO "CurriculumExerciseKindTypes" ("Id","Key") VALUES (1,'ci-quiz');
    INSERT INTO "CurriculumExercisePhaseTypes" ("Id","Key") VALUES (1,'ci-practice');
    INSERT INTO "CurriculumExercises"
        ("CurriculumLessonId","CurriculumBlueprintId","Key","Ordinal",
         "CurriculumExerciseKindTypeId","CurriculumExercisePhaseTypeId")
    VALUES (lesson_one,blueprint_one,'one',1,1,1)
    RETURNING "Id" INTO exercise_one;
    INSERT INTO "CurriculumExercises"
        ("CurriculumLessonId","CurriculumBlueprintId","Key","Ordinal",
         "CurriculumExerciseKindTypeId","CurriculumExercisePhaseTypeId")
    VALUES (lesson_two,blueprint_two,'two',1,1,1)
    RETURNING "Id" INTO exercise_two;

    INSERT INTO "SharedCourseInstances"
        ("CurriculumBlueprintId","SourceLanguage","TargetLanguage","Title",
         "ContentVersion","ContentFingerprint")
    VALUES (blueprint_one,'en','de','CI Course',1,repeat('a',64))
    RETURNING "Id" INTO shared_one;
    INSERT INTO "LearnerCourses"
        ("ProfileId","SharedCourseInstanceId","CurriculumBlueprintId")
    VALUES (profile_id,shared_one,blueprint_one)
    RETURNING "Id" INTO enrollment_one;

    INSERT INTO "LearnerProgressStatusTypes" ("Id","Key") VALUES (1,'ci-new');
    INSERT INTO "LearnerExerciseOutcomeTypes" ("Id","Key") VALUES (1,'ci-accepted');
    INSERT INTO "LearningActivityKindTypes" ("Id","Key") VALUES (1,'ci-learning');

    -- Positive control: an item inside the same pinned blueprint must succeed.
    INSERT INTO "LearnerCourseProgress"
        ("LearnerCourseId","CurriculumBlueprintId","CurriculumLevelId",
         "LearnerProgressStatusTypeId")
    VALUES (enrollment_one,blueprint_one,level_one,1);
    INSERT INTO "LearnerExerciseAttempts"
        ("LearnerCourseId","CurriculumBlueprintId","CurriculumExerciseId",
         "ClientEventId","LearnerExerciseOutcomeTypeId")
    VALUES (enrollment_one,blueprint_one,exercise_one,gen_random_uuid(),1);

    -- Negative #1: an attempt from Blueprint TWO must not write to Blueprint ONE.
    BEGIN
        INSERT INTO "LearnerExerciseAttempts"
            ("LearnerCourseId","CurriculumBlueprintId","CurriculumExerciseId",
             "ClientEventId","LearnerExerciseOutcomeTypeId")
        VALUES (enrollment_one,blueprint_one,exercise_two,gen_random_uuid(),1);
        RAISE EXCEPTION 'Cross-blueprint exercise attempt was accepted';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;

    -- Negative #2: progress must not reference a foreign level.
    BEGIN
        INSERT INTO "LearnerCourseProgress"
            ("LearnerCourseId","CurriculumBlueprintId","CurriculumLevelId",
             "LearnerProgressStatusTypeId")
        VALUES (enrollment_one,blueprint_one,level_two,1);
        RAISE EXCEPTION 'Cross-blueprint progress was accepted';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;

    -- Negative #3: SharedCourse content cannot embed an exercise of another blueprint.
    BEGIN
        INSERT INTO "SharedCourseExerciseContent"
            ("SharedCourseInstanceId","CurriculumBlueprintId",
             "CurriculumExerciseId","SchemaVersion","Payload","ContentFingerprint")
        VALUES (shared_one,blueprint_one,exercise_two,1,'{}'::jsonb,repeat('b',64));
        RAISE EXCEPTION 'Cross-blueprint shared course content was accepted';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;

    -- Negative #4: a real Lesson from a foreign Blueprint must not appear in
    -- LearningActivitySessions for the learner's pinned course.
    BEGIN
        INSERT INTO "LearningActivitySessions"
            ("ClientSessionId","ProfileId","LearningActivityKindTypeId",
             "LearnerCourseId","CurriculumBlueprintId","CurriculumLessonId",
             "StartedAt","LastActivityAt")
        VALUES (gen_random_uuid(),profile_id,1,enrollment_one,blueprint_one,
                lesson_two,now(),now());
        RAISE EXCEPTION 'Cross-blueprint learning activity was accepted';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;

    -- Positive control for progress subtype: one parent and matching detail.
    INSERT INTO "Works" ("MediaTypeId","CanonicalTitle")
    VALUES (1,'Phase B Work') RETURNING "Id" INTO work_id;
    INSERT INTO "MediaProgress"
        ("ProfileId","WorkId","ProgressPositionTypeId")
    VALUES (profile_id,work_id,1)
    RETURNING "Id" INTO progress_id;
    INSERT INTO "TimeProgressPositions" ("MediaProgressId","PositionMs")
    VALUES (progress_id,1200);

    -- Negative #5: Work with a time progress parent but no matching detail
    -- cannot satisfy the DEFERRABLE total-subtype FK at COMMIT.
    BEGIN
        INSERT INTO "MediaProgress"
            ("ProfileId","WorkId","ProgressPositionTypeId","WorkChapterId")
        VALUES (profile_id,work_id,1,NULL)
        ON CONFLICT DO NOTHING;
        -- This insert is a duplicate by exact-target uniqueness; use a different
        -- Work identity for the missing-position rejection.
        INSERT INTO "Works" ("MediaTypeId","CanonicalTitle")
        VALUES (1,'Phase B Missing Detail') RETURNING "Id" INTO work_id;
        INSERT INTO "MediaProgress"
            ("ProfileId","WorkId","ProgressPositionTypeId")
        VALUES (profile_id,work_id,1);
        EXECUTE 'SET CONSTRAINTS "FK_MediaProgress_TimeProgressPositions" IMMEDIATE';
        RAISE EXCEPTION 'Time progress without subtype was accepted';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;

    RAISE NOTICE 'Phase B positive and negative relational scope tests passed';
END $test$;
ROLLBACK;
