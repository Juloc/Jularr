-- Gate B: Learning v2 scope preference constraints and read hierarchy.
-- Execute only on disposable phase_b_scratch, roll back all fixtures.
BEGIN;
DO $phase_b_learning_scope$
DECLARE
    account_id bigint;
    other_account_id bigint;
    profile_id bigint;
    other_profile_id bigint;
    locale_id bigint;
    book_a bigint;
    book_b bigint;
    chapter_a bigint;
    chapter_b bigint;
    global_scope bigint;
    media_scope bigint;
    work_scope bigint;
    content_scope bigint;
BEGIN
    INSERT INTO "UiLocales" ("Locale","Name")
    VALUES ('phase-b-scope','Learning Scope Test')
    RETURNING "Id" INTO locale_id;
    INSERT INTO "Accounts" ("Email","DisplayName","AccountRoleTypeId")
    VALUES ('phase-b-learning-a@example.invalid','Scope Account A',1)
    RETURNING "Id" INTO account_id;
    INSERT INTO "Accounts" ("Email","DisplayName","AccountRoleTypeId")
    VALUES ('phase-b-learning-b@example.invalid','Scope Account B',1)
    RETURNING "Id" INTO other_account_id;
    INSERT INTO "Profiles" ("OwnerAccountId","DisplayName","UiLocaleId")
    VALUES (account_id,'Learning Scope A',locale_id)
    RETURNING "Id" INTO profile_id;
    INSERT INTO "Profiles" ("OwnerAccountId","DisplayName","UiLocaleId")
    VALUES (other_account_id,'Learning Scope B',locale_id)
    RETURNING "Id" INTO other_profile_id;
    INSERT INTO "AccountProfiles" ("AccountId","ProfileId")
    VALUES (account_id,profile_id),(other_account_id,other_profile_id);
    INSERT INTO "Works" ("MediaTypeId","CanonicalTitle")
    VALUES (3,'Phase B Learning Book A')
    RETURNING "Id" INTO book_a;
    INSERT INTO "Works" ("MediaTypeId","CanonicalTitle")
    VALUES (3,'Phase B Learning Book B')
    RETURNING "Id" INTO book_b;
    INSERT INTO "WorkChapters" ("WorkId","OrderIndex")
    VALUES (book_a,0)
    RETURNING "Id" INTO chapter_a;
    INSERT INTO "WorkChapters" ("WorkId","OrderIndex")
    VALUES (book_b,0)
    RETURNING "Id" INTO chapter_b;

    INSERT INTO "LearningScopeOverrides" ("ProfileId","LearningModeTypeId")
    VALUES (profile_id,2)
    RETURNING "Id" INTO global_scope;
    INSERT INTO "LearningScopeOverrides"
      ("ProfileId","LearningMediaScopeTypeId","LearningModeTypeId")
    VALUES (profile_id,3,0)
    RETURNING "Id" INTO media_scope;
    INSERT INTO "LearningScopeOverrides"
      ("ProfileId","LearningMediaScopeTypeId","WorkId","LearningModeTypeId")
    VALUES (profile_id,3,book_a,1)
    RETURNING "Id" INTO work_scope;
    INSERT INTO "LearningScopeOverrides"
      ("ProfileId","LearningMediaScopeTypeId","WorkId","WorkChapterId","LearningModeTypeId")
    VALUES (profile_id,3,book_a,chapter_a,3)
    RETURNING "Id" INTO content_scope;

    -- A capability override can exist even when its own scope has no mode.
    INSERT INTO "LearningScopeOverrides"
      ("ProfileId","LearningMediaScopeTypeId","WorkId","WorkChapterId")
    VALUES (profile_id,3,book_b,chapter_b);
    INSERT INTO "LearningCapabilityOverrides"
      ("LearningScopeOverrideId","LearningCapabilityTypeId","IsEnabled")
    VALUES (global_scope,6,true),(media_scope,6,false),
           (work_scope,3,true),(content_scope,6,true);

    IF (SELECT count(*) FROM "LearningScopeOverrides" WHERE "ProfileId"=profile_id) <> 5 THEN
        RAISE EXCEPTION 'Learning scope hierarchy has incorrect row count';
    END IF;
    IF (SELECT count(*) FROM "LearningCapabilityOverrides" WHERE "LearningScopeOverrideId"=content_scope) <> 1 THEN
        RAISE EXCEPTION 'Content-scoped capability is missing';
    END IF;

    BEGIN
        INSERT INTO "LearningScopeOverrides" ("ProfileId","LearningModeTypeId")
        VALUES (profile_id,3);
        RAISE EXCEPTION 'Duplicate nullable Profile scope allowed';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO "LearningScopeOverrides"
          ("ProfileId","LearningMediaScopeTypeId","WorkId","WorkChapterId","LearningModeTypeId")
        VALUES (profile_id,3,book_a,chapter_a,1);
        RAISE EXCEPTION 'Duplicate canonical content scope allowed';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO "LearningScopeOverrides"
          ("ProfileId","WorkId","LearningModeTypeId")
        VALUES (profile_id,book_a,1);
        RAISE EXCEPTION 'Missing explicit Learning media scope type allowed';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO "LearningScopeOverrides"
          ("ProfileId","LearningMediaScopeTypeId","WorkId","WorkChapterId")
        VALUES (profile_id,3,book_a,chapter_b);
        RAISE EXCEPTION 'Cross-Work content override allowed';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO "LearningScopeOverrides"
          ("ProfileId","LearningMediaScopeTypeId","LearningModeTypeId")
        VALUES (profile_id,3,255);
        RAISE EXCEPTION 'Unknown Learning mode byte allowed';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO "LearningCapabilityOverrides"
          ("LearningScopeOverrideId","LearningCapabilityTypeId","IsEnabled")
        VALUES (content_scope,6,false);
        RAISE EXCEPTION 'Duplicate capability override allowed';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO "LearningCapabilityOverrides"
          ("LearningScopeOverrideId","LearningCapabilityTypeId","IsEnabled")
        VALUES (content_scope,255,true);
        RAISE EXCEPTION 'Unknown capability byte allowed';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;

    IF (SELECT count(*) FROM "LearningScopeOverrides" WHERE "ProfileId"=other_profile_id) <> 0 THEN
        RAISE EXCEPTION 'Learning scope leaked into an unrelated profile';
    END IF;
    IF (SELECT count(*) FROM "LearningCapabilityTypes")<>14
       OR (SELECT count(*) FROM "LearningModeTypes")<>4
       OR (SELECT count(*) FROM "LearningMediaScopeTypes")<>4 THEN
        RAISE EXCEPTION 'Learning source enum seed set is incomplete';
    END IF;
    RAISE NOTICE 'Learning scope hierarchy, explicit inheritance, canonical unit FK and type values passed';
END $phase_b_learning_scope$;
ROLLBACK;
