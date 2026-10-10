-- DESTRUCTIVE TEST FIXTURES: ONLY run on fresh, disposable Phase-B scratch PostgreSQL.
-- DO NOT execute against dev/production. All inserts roll back after test.
-- The cutover DDL and exact type seeds must be installed first.
-- These are real negative FK/permission-scope rejection tests, not just catalog checks.
BEGIN;
DO $test$
DECLARE
    account_id bigint;
    locale_id bigint;
    profile_id bigint;
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
    title_provider_id bigint;
    person_id bigint;
    season_one bigint;
    episode_one bigint;
    rejected_constraint text;
    result_case record;
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

    INSERT INTO "AccountPasskeys"
        ("AccountId","CredentialId","PublicKey","Aaguid","Transports",
         "BackupEligible","BackedUp","UserVerificationRequired")
    VALUES (account_id,decode('0101','hex'),decode('0202','hex'),
            '00000000-0000-0000-0000-000000000001',ARRAY['internal'],true,true,true);
    BEGIN
        INSERT INTO "AccountPasskeys"
            ("AccountId","CredentialId","PublicKey","BackupEligible","BackedUp")
        VALUES (account_id,decode('0303','hex'),decode('0404','hex'),false,true);
        RAISE EXCEPTION 'Backed-up ineligible credential was accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    -- Positive control for progress subtype: one parent and matching detail.
    INSERT INTO "Works" ("MediaTypeId","CanonicalTitle")
    VALUES (1,'Phase B Work') RETURNING "Id" INTO work_id;
    INSERT INTO "WorkMetadataFacts"
        ("WorkId","FirstPublishedOn","RuntimeMs","CommunityRating",
         "CommunityRatingCount","Certification","CertificationCountry",
         "Studios","ProductionCountries")
    VALUES (work_id,DATE '2025-01-01',3600000,8.5,40,'PG','US',
            ARRAY['phase-b-studio'],ARRAY['US']);
    BEGIN
        UPDATE "WorkMetadataFacts" SET "CommunityRating" = 11
        WHERE "WorkId" = work_id;
        RAISE EXCEPTION 'Out-of-range community rating was accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    INSERT INTO "People" ("DisplayName") VALUES ('Phase B Performer')
    RETURNING "Id" INTO person_id;
    INSERT INTO "MusicArtists" ("DisplayName","PersonId")
    VALUES ('Phase B Solo Artist',person_id);
    INSERT INTO "MusicArtists" ("DisplayName") VALUES ('Phase B Ensemble');
    BEGIN
        INSERT INTO "MusicArtists" ("DisplayName","PersonId")
        VALUES ('Invalid Performer',-1);
        RAISE EXCEPTION 'MusicArtists accepted an unknown PersonId';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;
    INSERT INTO "Providers" ("Key") VALUES ('phase-b-title-source')
    RETURNING "Id" INTO title_provider_id;
    INSERT INTO "WorkTitles"
        ("WorkId","Title","ProviderId","SourceReference","IsManualOverride")
    VALUES (work_id,'Phase B Title',title_provider_id,'test-source-title',false);
    BEGIN
        INSERT INTO "WorkTitles" ("WorkId","Title","ProviderId")
        VALUES (work_id,'Invalid title source',-1);
        RAISE EXCEPTION 'WorkTitles accepted an unknown source provider';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;
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
        VALUES (account_id,other_profile_id,decode(repeat('de',32),'hex'),now()+interval '1 day');
        RAISE EXCEPTION 'Cross-account Profile was accepted as active session';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;

    -- Positive cover assignment, then reject one image assigned to two targets
    -- in a single row. A second invalid case proves Type/Target-kind pairing.

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

    INSERT INTO "MediaDetectionRuns"
        ("MediaAssetId","MediaDetectionTypeId","MediaDetectionStatusTypeId",
         "InputFingerprint","DetectorVersion","MatchCount","FinishedAt")
    VALUES (asset_one,1,2,'ci-fixture-fingerprint','ci-v1',0,now()),
           (asset_one,1,2,'ci-fixture-fingerprint','ci-v1',0,now());
    -- Success with MatchCount=0 must NOT be forced into a failed run.
    INSERT INTO "MediaDetectionRuns" (
        "MediaAssetId", "MediaDetectionTypeId", "MediaDetectionStatusTypeId",
        "InputFingerprint", "DetectorVersion", "FinishedAt", "ErrorCode")
    VALUES (asset_one, 1, 1, 'running', 'v1', NULL, NULL),
           (asset_one, 1, 3, 'failed', 'v1', now(), 'probe_failed'),
           (asset_one, 1, 4, 'cancelled', 'v1', now(), NULL);
    FOR result_case IN
        SELECT fixture.status, fixture.finished_at, fixture.match_count, fixture.error_code
        FROM (VALUES
            (1::smallint, now(), 0, NULL::text),
            (2::smallint, NULL::timestamptz, 0, NULL::text),
            (3::smallint, now(), 0, 'probe_failed'),
            (4::smallint, now(), 0, NULL::text)) AS fixture(status, finished_at, match_count, error_code)
    LOOP
        BEGIN
            INSERT INTO "MediaDetectionRuns" (
                "MediaAssetId", "MediaDetectionTypeId", "MediaDetectionStatusTypeId",
                "InputFingerprint", "DetectorVersion", "FinishedAt", "MatchCount", "ErrorCode")
            VALUES (asset_one, 1, result_case.status, 'invalid', 'v1',
                    result_case.finished_at, result_case.match_count, result_case.error_code);
            RAISE EXCEPTION 'Detection status % accepted an inconsistent result', result_case.status;
        EXCEPTION WHEN check_violation THEN
            GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
            IF rejected_constraint <> 'CK_MediaDetectionRuns_Result' THEN
                RAISE;
            END IF;
        END;
    END LOOP;

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
