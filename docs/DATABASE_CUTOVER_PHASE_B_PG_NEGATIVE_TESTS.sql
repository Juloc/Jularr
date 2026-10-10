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
    blueprint_one bigint;
    blueprint_two bigint;
    level_one bigint;
    level_two bigint;
    chapter_one bigint;
    chapter_two bigint;
    lesson_one bigint;
    lesson_two bigint;
    exercise_one bigint;
    exercise_two bigint;
    shared_one bigint;
    enrollment_one bigint;
    progress_id bigint;
    work_id bigint;
    work_for_image bigint;
    other_account_id bigint;
    other_profile_id bigint;
    image_id bigint;
    chapter_id bigint;
    asset_one bigint;
    asset_other bigint;
    file_one bigint;
    file_two bigint;
    library_root_id bigint;
    edition_one bigint;
    edition_two bigint;
    version_one bigint;
    version_two bigint;
    other_work bigint;
    game_platform_id bigint;
    season_one bigint;
    episode_one bigint;
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
    work_for_image := work_id;

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

    -- Negative #6: Account A cannot select another Account B's Profile in its session.
    INSERT INTO "Accounts" ("Email","DisplayName","AccountRoleTypeId")
    VALUES ('phase-b-other@example.invalid','Other Account',2)
    RETURNING "Id" INTO other_account_id;
    INSERT INTO "Profiles" ("OwnerAccountId","DisplayName","UiLocaleId")
    VALUES (other_account_id,'Other Profile',locale_id)
    RETURNING "Id" INTO other_profile_id;
    INSERT INTO "AccountProfiles" ("AccountId","ProfileId")
    VALUES (other_account_id,other_profile_id);

    BEGIN
        INSERT INTO "AccountSessions"
            ("AccountId","ActiveProfileId","TokenHash","ExpiresAt")
        VALUES (account_id,other_profile_id,decode('deadbeef','hex'),now()+interval '1 day');
        RAISE EXCEPTION 'Cross-account Profile was accepted as active session';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;

    -- Positive cover assignment, then reject one image assigned to two targets
    -- in a single row. A second invalid case proves Type/Target-kind pairing.
    INSERT INTO "ImageTypes" ("Id","Key") VALUES (1,'ci-cover');
    INSERT INTO "ImageTypeTargets" ("ImageTypeId","ImageTargetKindTypeId")
    VALUES (1,1);
    INSERT INTO "Images" ("StorageKey","MimeType")
    VALUES ('phase-b-ci-image','image/png') RETURNING "Id" INTO image_id;
    INSERT INTO "ImageAssignments"
        ("ImageId","ImageTypeId","ImageTargetKindTypeId","WorkId")
    VALUES (image_id,1,1,work_for_image);
    INSERT INTO "WorkChapters" ("WorkId","OrderIndex","DisplayName")
    VALUES (work_for_image,1,'CI chapter') RETURNING "Id" INTO chapter_id;

    -- Negative #7: exactly one image target, even if both targets exist.
    BEGIN
        INSERT INTO "ImageAssignments"
            ("ImageId","ImageTypeId","ImageTargetKindTypeId","WorkId","WorkChapterId")
        VALUES (image_id,1,1,work_for_image,chapter_id);
        RAISE EXCEPTION 'ImageAssignment with two targets was accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    -- Negative #8: Work-kind image type may not be used on a Chapter target.
    BEGIN
        INSERT INTO "ImageAssignments"
            ("ImageId","ImageTypeId","ImageTargetKindTypeId","WorkChapterId")
        VALUES (image_id,1,1,chapter_id);
        RAISE EXCEPTION 'ImageAssignment with incorrect target kind was accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;


    -- T02: an owner-less Profile MUST NOT survive deferred membership validation.
    BEGIN
        INSERT INTO "Profiles" ("OwnerAccountId","DisplayName","UiLocaleId")
        VALUES (account_id,'Missing Owner Membership',locale_id);
        SET CONSTRAINTS "FK_Profiles_OwnerAccountProfiles" IMMEDIATE;
        RAISE EXCEPTION 'Profile without owner membership was accepted';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;

    -- Build two separate Works and fully consistent media/version/file chains.
    -- The media and file-role codes are SCRATCH-ONLY; target enum seeds remain unsigned.
    INSERT INTO "Works" ("MediaTypeId","CanonicalTitle")
    VALUES (7,'Phase B Game Work') RETURNING "Id" INTO other_work;
    INSERT INTO "WorkEditions" ("WorkId","Name") VALUES (work_for_image,'Movie Edition')
        RETURNING "Id" INTO edition_one;
    INSERT INTO "WorkEditions" ("WorkId","Name") VALUES (other_work,'Game Edition')
        RETURNING "Id" INTO edition_two;
    INSERT INTO "WorkVersions" ("WorkEditionId","WorkId","VersionLabel")
    VALUES (edition_one,work_for_image,'Movie v1') RETURNING "Id" INTO version_one;
    INSERT INTO "WorkVersions" ("WorkEditionId","WorkId","VersionLabel")
    VALUES (edition_two,other_work,'Game v1') RETURNING "Id" INTO version_two;
    INSERT INTO "MediaAssetTypes" ("Id","Key") VALUES (1,'ci-file');

    -- T04: an asset cannot bind a WorkVersion to somebody else's Work.
    BEGIN
        INSERT INTO "MediaAssets" ("WorkVersionId","WorkId","MediaAssetTypeId")
        VALUES (version_one,other_work,1);
        RAISE EXCEPTION 'Cross-Work media asset was accepted';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;

    INSERT INTO "MediaAssets" ("WorkVersionId","WorkId","MediaAssetTypeId")
    VALUES (version_one,work_for_image,1) RETURNING "Id" INTO asset_one;
    INSERT INTO "MediaAssets" ("WorkVersionId","WorkId","MediaAssetTypeId")
    VALUES (version_two,other_work,1) RETURNING "Id" INTO asset_other;
    INSERT INTO "LibraryRoots" ("DisplayName","RootPath")
    VALUES ('Phase B Root','/phase-b-ci') RETURNING "Id" INTO library_root_id;

    -- T05: a StoredFile cannot assert a version differing from its MediaAsset.
    BEGIN
        INSERT INTO "StoredFiles"
            ("LibraryRootId","MediaAssetId","WorkVersionId","NormalizedRelativePath")
        VALUES (library_root_id,asset_one,version_two,'ci-bad-version');
        RAISE EXCEPTION 'Cross-Version stored file was accepted';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;

    INSERT INTO "StoredFiles"
        ("LibraryRootId","MediaAssetId","WorkVersionId","NormalizedRelativePath","IsPresent")
    VALUES (library_root_id,asset_one,version_one,'ci-movie-file',true)
    RETURNING "Id" INTO file_one;
    INSERT INTO "StoredFiles"
        ("LibraryRootId","MediaAssetId","WorkVersionId","NormalizedRelativePath","IsPresent")
    VALUES (library_root_id,asset_other,version_two,'ci-game-disc-1',true)
    RETURNING "Id" INTO file_two;

    -- T06: game release files must come from precisely the release's WorkVersion.
    INSERT INTO "GamePlatforms" ("Key","Name") VALUES ('ci-platform','Test Platform')
        RETURNING "Id" INTO game_platform_id;
    INSERT INTO "GameReleases" ("WorkVersionId","WorkId","GamePlatformId")
    VALUES (version_two,other_work,game_platform_id);
    -- T07 negative control 1: a Movie WorkVersion is never a GameRelease,
    -- even when the WorkVersion/WorkId pair itself is consistent.
    BEGIN
        INSERT INTO "GameReleases" ("WorkVersionId","WorkId","GamePlatformId")
        VALUES (version_one,work_for_image,game_platform_id);
        RAISE EXCEPTION 'Non-game WorkVersion was accepted as GameRelease';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;
    -- T07 negative control 2: a Game-typed Work cannot borrow a Movie version.
    BEGIN
        INSERT INTO "GameReleases" ("WorkVersionId","WorkId","GamePlatformId")
        VALUES (version_one,other_work,game_platform_id);
        RAISE EXCEPTION 'GameRelease with a foreign WorkVersion was accepted';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;
    INSERT INTO "GameReleaseFileRoleTypes" ("Id","Key") VALUES (1,'ci-disc');
    INSERT INTO "GameReleaseStoredFiles"
        ("WorkVersionId","StoredFileId","DiscNumber","GameReleaseFileRoleTypeId")
    VALUES (version_two,file_two,1,1);
    BEGIN
        INSERT INTO "GameReleaseStoredFiles"
            ("WorkVersionId","StoredFileId","DiscNumber","GameReleaseFileRoleTypeId")
        VALUES (version_two,file_one,2,1);
        RAISE EXCEPTION 'Foreign WorkVersion file was accepted for game release';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;
    -- T07 draft relational guarantee holds with PROPOSED MediaType(game)=7;
    -- B01 byte-enum/seed signoff remains mandatory.

    -- T08: neither the Work nor the selected File may point to a different asset.
    BEGIN
        INSERT INTO "PlaybackSessions"
            ("ProfileId","WorkId","MediaAssetId","PlaybackModeKey")
        VALUES (profile_id,work_for_image,asset_other,'ci-direct');
        RAISE EXCEPTION 'PlaybackSession with foreign Work/Asset was accepted';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO "PlaybackSessions"
            ("ProfileId","WorkId","MediaAssetId","StoredFileId","PlaybackModeKey")
        VALUES (profile_id,other_work,asset_other,file_one,'ci-direct');
        RAISE EXCEPTION 'PlaybackSession with foreign StoredFile was accepted';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;

    -- T10: media progress Episode FK must resolve inside its declared Work.
    INSERT INTO "WorkSeasons" ("WorkId","SeasonNumber")
    VALUES (work_for_image,1) RETURNING "Id" INTO season_one;
    INSERT INTO "WorkEpisodes" ("WorkId","WorkSeasonId","OrderIndex")
    VALUES (work_for_image,season_one,1) RETURNING "Id" INTO episode_one;
    BEGIN
        INSERT INTO "MediaProgress"
            ("ProfileId","WorkId","WorkEpisodeId","ProgressPositionTypeId")
        VALUES (profile_id,other_work,episode_one,1);
        RAISE EXCEPTION 'MediaProgress to another Works Episode was accepted';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;

    -- T11: NULLS NOT DISTINCT must reject two root progress rows for same profile/work.
    BEGIN
        INSERT INTO "MediaProgress" ("ProfileId","WorkId","ProgressPositionTypeId")
        VALUES (profile_id,work_for_image,1);
        RAISE EXCEPTION 'Duplicate root MediaProgress was accepted';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;

    -- T13: overlapping valid segments are permitted, reversed time is prohibited.
    INSERT INTO "MediaSegmentSourceTypes" ("Id","Key") VALUES (1,'ci-manual');
    INSERT INTO "MediaSegments"
        ("MediaAssetId","MediaSegmentTypeId","MediaSegmentSourceTypeId","StartMs","EndMs")
    VALUES (asset_one,1,1,0,1200),(asset_one,1,1,800,1500);
    BEGIN
        INSERT INTO "MediaSegments"
            ("MediaAssetId","MediaSegmentTypeId","MediaSegmentSourceTypeId","StartMs","EndMs")
        VALUES (asset_one,1,1,900,100);
        RAISE EXCEPTION 'Reverse-time MediaSegment was accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    INSERT INTO "MediaDetectionTypes" ("Id","Key") VALUES (1,'ci-intro');
    INSERT INTO "MediaDetectionStatusTypes" ("Id","Key") VALUES (1,'ci-success');
    INSERT INTO "MediaDetectionRuns"
        ("MediaAssetId","MediaDetectionTypeId","MediaDetectionStatusTypeId",
         "InputFingerprint","DetectorVersion","MatchCount","FinishedAt")
    VALUES (asset_one,1,1,'ci-fixture-fingerprint','ci-v1',0,now());
    -- Success with MatchCount=0 must NOT be forced into a failed run.

    -- T20: canonical Wanted follows the Work/Unit identity, not an unrelated Work.
    BEGIN
        INSERT INTO "WantedItems" ("WorkId","WorkEpisodeId")
        VALUES (other_work,episode_one);
        RAISE EXCEPTION 'Wanted item for foreign episode was accepted';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;
    INSERT INTO "WantedItems" ("WorkId") VALUES (other_work);
    BEGIN
        INSERT INTO "WantedItems" ("WorkId") VALUES (other_work);
        RAISE EXCEPTION 'Duplicate wanted root with NULL unit columns was accepted';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;

    RAISE NOTICE 'Phase B positive and negative relational scope tests passed';
END $test$;
ROLLBACK;
