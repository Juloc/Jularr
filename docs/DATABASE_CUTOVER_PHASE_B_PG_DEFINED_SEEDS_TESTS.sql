BEGIN TRANSACTION READ ONLY;
DO $target_seed_contract$
DECLARE
    mismatch text;
BEGIN
    WITH expected(target_table, id, key) AS (
        VALUES
            ('AccountRoleTypes', 1, 'owner'),
            ('AccountRoleTypes', 2, 'user'),
            ('AccountRoleTypes', 3, 'media_manager'),
            ('MediaTypes', 1, 'movie'),
            ('MediaTypes', 2, 'series'),
            ('MediaTypes', 3, 'book'),
            ('MediaTypes', 4, 'manga'),
            ('MediaTypes', 5, 'light_novel'),
            ('MediaTypes', 6, 'music'),
            ('MediaTypes', 7, 'game'),
            ('ProgressPositionTypes', 1, 'time'),
            ('ProgressPositionTypes', 2, 'reading'),
            ('ProgressPositionTypes', 3, 'game'),
            ('ImageTargetKindTypes', 1, 'work'),
            ('ImageTargetKindTypes', 2, 'work_chapter'),
            ('ImageTargetKindTypes', 3, 'media_chapter'),
            ('MediaSegmentTypes', 1, 'intro'),
            ('MediaSegmentTypes', 2, 'recap'),
            ('MediaSegmentTypes', 3, 'outro'),
            ('MediaSegmentTypes', 4, 'credits'),
            ('MediaSegmentTypes', 5, 'preview'),
            ('OperationStatusTypes', 1, 'queued'),
            ('OperationStatusTypes', 2, 'running'),
            ('OperationStatusTypes', 3, 'succeeded'),
            ('OperationStatusTypes', 4, 'failed'),
            ('OperationStatusTypes', 5, 'cancelled'),
            ('OperationStatusTypes', 6, 'interrupted'),
            ('AcquisitionRuleFieldTypes', 0, 'raw_title'),
            ('AcquisitionRuleFieldTypes', 1, 'release_group'),
            ('AcquisitionRuleFieldTypes', 2, 'source'),
            ('AcquisitionRuleFieldTypes', 3, 'resolution'),
            ('AcquisitionRuleFieldTypes', 4, 'video_codec'),
            ('AcquisitionRuleFieldTypes', 5, 'bit_depth'),
            ('AcquisitionRuleFieldTypes', 6, 'hdr_format'),
            ('AcquisitionRuleFieldTypes', 7, 'audio_codec'),
            ('AcquisitionRuleFieldTypes', 8, 'audio_language'),
            ('AcquisitionRuleFieldTypes', 9, 'subtitle_language'),
            ('AcquisitionRuleFieldTypes', 10, 'dual_audio'),
            ('AcquisitionRuleFieldTypes', 11, 'multi_audio'),
            ('AcquisitionRuleFieldTypes', 12, 'proper'),
            ('AcquisitionRuleFieldTypes', 13, 'repack'),
            ('AcquisitionRuleFieldTypes', 14, 'indexer'),
            ('AcquisitionRuleMatchTypes', 0, 'equals'),
            ('AcquisitionRuleMatchTypes', 1, 'contains'),
            ('AcquisitionRuleMatchTypes', 2, 'regex'),
            ('AcquisitionRuleEffectTypes', 0, 'prefer'),
            ('AcquisitionRuleEffectTypes', 1, 'avoid'),
            ('AcquisitionRuleEffectTypes', 2, 'require'),
            ('AcquisitionRuleEffectTypes', 3, 'reject'),
            ('AcquisitionRuleEffectTypes', 4, 'info'),
            ('LearningUnitKindTypes', 1, 'word'),
            ('LearningUnitKindTypes', 2, 'sentence'),
            ('LearningUnitKindTypes', 3, 'script'),
            ('LearningVariantRoleTypes', 1, 'primary'),
            ('LearningVariantRoleTypes', 2, 'meaning'),
            ('LearningVariantSourceTypes', 1, 'manual'),
            ('LearningVariantSourceTypes', 2, 'term'),
            ('LearningVariantSourceTypes', 3, 'dictionary'),
            ('LearningVariantSourceTypes', 4, 'script_catalog'),
            ('LearningCardModeTypes', 1, 'recognition'),
            ('LearningCardModeTypes', 2, 'production'),
            ('LearningCardModeTypes', 3, 'listening'),
            ('LearningCardModeTypes', 4, 'writing'),
            ('LearningCardStateTypes', 1, 'known'),
            ('LearningCardStateTypes', 2, 'learning'),
            ('LearningCardStateTypes', 3, 'saved'),
            ('LearningCardStateTypes', 4, 'ignored'),
            ('LearningCardStateTypes', 5, 'suspended'),
            ('LearningCardReviewRatingTypes', 1, 'again'),
            ('LearningCardReviewRatingTypes', 2, 'hard'),
            ('LearningCardReviewRatingTypes', 3, 'good'),
            ('LearningCardReviewRatingTypes', 4, 'easy'),
            ('LearningModeTypes', 0, 'off'),
            ('LearningModeTypes', 1, 'language_tools'),
            ('LearningModeTypes', 2, 'study'),
            ('LearningModeTypes', 3, 'custom'),
            ('LearningCapabilityTypes', 1, 'language_lookup'),
            ('LearningCapabilityTypes', 2, 'reading_aids'),
            ('LearningCapabilityTypes', 3, 'translation'),
            ('LearningCapabilityTypes', 4, 'ai_explanations'),
            ('LearningCapabilityTypes', 5, 'vocabulary'),
            ('LearningCapabilityTypes', 6, 'reviews'),
            ('LearningCapabilityTypes', 7, 'sentence_practice'),
            ('LearningCapabilityTypes', 8, 'script_trainer'),
            ('LearningCapabilityTypes', 9, 'progress'),
            ('LearningCapabilityTypes', 10, 'home_widget'),
            ('LearningCapabilityTypes', 11, 'content_metrics'),
            ('LearningCapabilityTypes', 12, 'preparation_suggestions'),
            ('LearningCapabilityTypes', 13, 'player_tools'),
            ('LearningCapabilityTypes', 14, 'reader_tools'),
            ('LearningMediaScopeTypes', 1, 'anime'),
            ('LearningMediaScopeTypes', 2, 'novel'),
            ('LearningMediaScopeTypes', 3, 'book'),
            ('LearningMediaScopeTypes', 4, 'manga'),
            ('NotificationEventCategoryTypes', 1, 'download_grabbed'),
            ('NotificationEventCategoryTypes', 2, 'download_failed'),
            ('NotificationEventCategoryTypes', 3, 'import_completed'),
            ('NotificationEventCategoryTypes', 4, 'import_failed'),
            ('NotificationEventCategoryTypes', 5, 'release_available'),
            ('NotificationEventCategoryTypes', 6, 'request_approved'),
            ('NotificationEventCategoryTypes', 7, 'request_denied'),
            ('NotificationEventCategoryTypes', 8, 'storage_problem'),
            ('EventAudienceTypes', 1, 'profile'),
            ('EventAudienceTypes', 2, 'admin'),
            ('EventSeverityTypes', 1, 'info'),
            ('EventSeverityTypes', 2, 'warning'),
            ('EventSeverityTypes', 3, 'critical'),
            ('NotificationChannelTypes', 1, 'in_app'),
            ('NotificationChannelTypes', 2, 'push'),
            ('NotificationChannelTypes', 3, 'email'),
            ('NotificationTimingTypes', 1, 'immediate'),
            ('NotificationTimingTypes', 2, 'digest'),
            ('AcquisitionIndexerTypeTypes', 1, 'prowlarr'),
            ('AcquisitionIndexerTypeTypes', 2, 'newznab'),
            ('AcquisitionKindTypes', 1, 'anime'),
            ('AcquisitionKindTypes', 2, 'manga'),
            ('AcquisitionKindTypes', 3, 'light_novel'),
            ('AcquisitionKindTypes', 4, 'book'),
            ('AcquisitionKindTypes', 5, 'movie'),
            ('AcquisitionKindTypes', 6, 'tv'),
            ('AcquisitionKindTypes', 7, 'audiobook'),
            ('AcquisitionKindTypes', 8, 'music'),
            ('AcquisitionRequestStatusTypes', 1, 'pending'),
            ('AcquisitionRequestStatusTypes', 2, 'approved'),
            ('AcquisitionRequestStatusTypes', 3, 'searching'),
            ('AcquisitionRequestStatusTypes', 4, 'downloading'),
            ('AcquisitionRequestStatusTypes', 5, 'importing'),
            ('AcquisitionRequestStatusTypes', 6, 'completed'),
            ('AcquisitionRequestStatusTypes', 7, 'rejected'),
            ('AcquisitionRequestStatusTypes', 8, 'failed'),
            ('CurriculumExerciseKindTypes', 1, 'presentation'),
            ('CurriculumExerciseKindTypes', 2, 'multiple_choice'),
            ('CurriculumExerciseKindTypes', 3, 'matching'),
            ('CurriculumExerciseKindTypes', 4, 'cloze'),
            ('CurriculumExerciseKindTypes', 5, 'ordering'),
            ('CurriculumExerciseKindTypes', 6, 'short_answer'),
            ('CurriculumExercisePhaseTypes', 1, 'orient'),
            ('CurriculumExercisePhaseTypes', 2, 'introduce'),
            ('CurriculumExercisePhaseTypes', 3, 'example'),
            ('CurriculumExercisePhaseTypes', 4, 'guided_practice'),
            ('CurriculumExercisePhaseTypes', 5, 'independent_retrieval'),
            ('CurriculumExercisePhaseTypes', 6, 'apply'),
            ('CurriculumExercisePhaseTypes', 7, 'checkpoint'),
            ('CurriculumExercisePhaseTypes', 8, 'summary'),
            ('CurriculumObjectiveSkillTypes', 1, 'vocabulary'),
            ('CurriculumObjectiveSkillTypes', 2, 'grammar'),
            ('CurriculumObjectiveSkillTypes', 3, 'reading'),
            ('CurriculumObjectiveSkillTypes', 4, 'listening'),
            ('CurriculumObjectiveSkillTypes', 5, 'script'),
            ('CurriculumObjectiveSkillTypes', 6, 'production'),
            ('CurriculumObjectiveSkillTypes', 7, 'writing'),
            ('CurriculumObjectiveSkillTypes', 8, 'speaking'),
            ('LearnerProgressStatusTypes', 1, 'not_started'),
            ('LearnerProgressStatusTypes', 2, 'in_progress'),
            ('LearnerProgressStatusTypes', 3, 'completed'),
            ('LearnerProgressStatusTypes', 4, 'skipped'),
            ('LearnerProgressSkipReasonTypes', 1, 'optional'),
            ('LearnerProgressSkipReasonTypes', 2, 'capability_unavailable'),
            ('LearnerExerciseOutcomeTypes', 1, 'completed'),
            ('LearnerExerciseOutcomeTypes', 2, 'correct'),
            ('LearnerExerciseOutcomeTypes', 3, 'incorrect'),
            ('LearnerExerciseOutcomeTypes', 4, 'partial'),
            ('LearningActivityKindTypes', 1, 'lesson'),
            ('LearningActivityKindTypes', 2, 'review'),
            ('LearningActivityKindTypes', 3, 'vocabulary_practice'),
            ('LearningActivityKindTypes', 4, 'sentence_practice'),
            ('LearningActivityKindTypes', 5, 'script_practice'),
            ('LearningActivityKindTypes', 6, 'media_practice'),
            ('LearningSessionEndReasonTypes', 1, 'completed'),
            ('LearningSessionEndReasonTypes', 2, 'exited'),
            ('LearningSessionEndReasonTypes', 3, 'abandoned'),
            ('LearningActivityEventKindTypes', 1, 'exercise_completed'),
            ('LearningActivityEventKindTypes', 2, 'review_completed'),
            ('LearningActivityEventKindTypes', 3, 'lesson_completed'),
            ('LearningActivityEventKindTypes', 4, 'practice_item_completed'),
            ('LearningActivityEventKindTypes', 5, 'session_completed'),
            ('GameReleaseFileRoleTypes', 1, 'primary'),
            ('GameReleaseFileRoleTypes', 2, 'disc'),
            ('GameReleaseFileRoleTypes', 3, 'track'),
            ('GameReleaseFileRoleTypes', 4, 'auxiliary'),
            ('MediaSegmentSourceTypes', 1, 'manual'),
            ('MediaSegmentSourceTypes', 2, 'imported'),
            ('MediaSegmentSourceTypes', 3, 'provider'),
            ('MediaSegmentSourceTypes', 4, 'detector'),
            ('MediaTrackTypes', 1, 'audio'),
            ('MediaTrackTypes', 2, 'subtitle'),
            ('MediaTrackTypes', 3, 'video'),
            ('WorkRelationTypes', 1, 'prequel'),
            ('WorkRelationTypes', 2, 'sequel'),
            ('WorkRelationTypes', 3, 'parent'),
            ('WorkRelationTypes', 4, 'side_story'),
            ('WorkRelationTypes', 5, 'alternative'),
            ('WorkRelationTypes', 6, 'spin_off'),
            ('WorkRelationTypes', 7, 'adaptation'),
            ('WorkRelationTypes', 8, 'source'),
            ('WorkRelationTypes', 9, 'summary'),
            ('WorkRelationTypes', 10, 'full_story'),
            ('WorkRelationTypes', 11, 'character'),
            ('WorkRelationTypes', 12, 'contains'),
            ('WorkRelationTypes', 13, 'remake'),
            ('WorkRelationTypes', 14, 'other'),
            ('ImageTypes', 1, 'cover'),
            ('ImageTypes', 2, 'banner'),
            ('ImageTypes', 3, 'poster'),
            ('ImageTypes', 4, 'backdrop'),
            ('ImageTypes', 5, 'logo'),
            ('ImageTypes', 6, 'screenshot'),
            ('ImageTypes', 7, 'title_screen'),
            ('MediaAssetTypes', 1, 'video'),
            ('MediaAssetTypes', 2, 'audio'),
            ('MediaAssetTypes', 3, 'ebook'),
            ('MediaAssetTypes', 4, 'comic_archive'),
            ('MediaAssetTypes', 5, 'subtitle'),
            ('MediaAssetTypes', 6, 'image'),
            ('MediaAssetTypes', 7, 'game_binary'),
            ('MediaDetectionTypes', 1, 'audio_fingerprint'),
            ('MediaDetectionStatusTypes', 1, 'running'),
            ('MediaDetectionStatusTypes', 2, 'succeeded'),
            ('MediaDetectionStatusTypes', 3, 'failed'),
            ('MediaDetectionStatusTypes', 4, 'cancelled'),
            ('WorkCreditRoleTypes', 1, 'cast'),
            ('WorkCreditRoleTypes', 2, 'crew'),
            ('WorkCreditRoleTypes', 3, 'author'),
            ('WorkCreditRoleTypes', 4, 'narrator'),
            ('WorkCreditRoleTypes', 5, 'illustrator'),
            ('WorkFactTypes', 1, 'first_published_on'),
            ('WorkFactTypes', 2, 'runtime_ms'),
            ('WorkFactTypes', 3, 'original_language'),
            ('WorkFactTypes', 4, 'community_rating'),
            ('WorkFactTypes', 5, 'community_rating_count'),
            ('WorkFactTypes', 6, 'certification'),
            ('WorkFactTypes', 7, 'certification_country'),
            ('WorkFactTypes', 8, 'studios'),
            ('WorkFactTypes', 9, 'production_countries'),
            ('WorkMediaClassificationTypes', 1, 'anime')
    ),
    actual(target_table, id, key) AS (
        SELECT
            'AccountRoleTypes',
            catalog."Id",
            catalog."Key"
        FROM "AccountRoleTypes" AS catalog
        UNION ALL
        SELECT
            'MediaTypes',
            catalog."Id",
            catalog."Key"
        FROM "MediaTypes" AS catalog
        UNION ALL
        SELECT
            'ProgressPositionTypes',
            catalog."Id",
            catalog."Key"
        FROM "ProgressPositionTypes" AS catalog
        UNION ALL
        SELECT
            'ImageTargetKindTypes',
            catalog."Id",
            catalog."Key"
        FROM "ImageTargetKindTypes" AS catalog
        UNION ALL
        SELECT
            'MediaSegmentTypes',
            catalog."Id",
            catalog."Key"
        FROM "MediaSegmentTypes" AS catalog
        UNION ALL
        SELECT
            'OperationStatusTypes',
            catalog."Id",
            catalog."Key"
        FROM "OperationStatusTypes" AS catalog
        UNION ALL
        SELECT
            'AcquisitionRuleFieldTypes',
            catalog."Id",
            catalog."Key"
        FROM "AcquisitionRuleFieldTypes" AS catalog
        UNION ALL
        SELECT
            'AcquisitionRuleMatchTypes',
            catalog."Id",
            catalog."Key"
        FROM "AcquisitionRuleMatchTypes" AS catalog
        UNION ALL
        SELECT
            'AcquisitionRuleEffectTypes',
            catalog."Id",
            catalog."Key"
        FROM "AcquisitionRuleEffectTypes" AS catalog
        UNION ALL
        SELECT
            'LearningUnitKindTypes',
            catalog."Id",
            catalog."Key"
        FROM "LearningUnitKindTypes" AS catalog
        UNION ALL
        SELECT
            'LearningVariantRoleTypes',
            catalog."Id",
            catalog."Key"
        FROM "LearningVariantRoleTypes" AS catalog
        UNION ALL
        SELECT
            'LearningVariantSourceTypes',
            catalog."Id",
            catalog."Key"
        FROM "LearningVariantSourceTypes" AS catalog
        UNION ALL
        SELECT
            'LearningCardModeTypes',
            catalog."Id",
            catalog."Key"
        FROM "LearningCardModeTypes" AS catalog
        UNION ALL
        SELECT
            'LearningCardStateTypes',
            catalog."Id",
            catalog."Key"
        FROM "LearningCardStateTypes" AS catalog
        UNION ALL
        SELECT
            'LearningCardReviewRatingTypes',
            catalog."Id",
            catalog."Key"
        FROM "LearningCardReviewRatingTypes" AS catalog
        UNION ALL
        SELECT
            'LearningModeTypes',
            catalog."Id",
            catalog."Key"
        FROM "LearningModeTypes" AS catalog
        UNION ALL
        SELECT
            'LearningCapabilityTypes',
            catalog."Id",
            catalog."Key"
        FROM "LearningCapabilityTypes" AS catalog
        UNION ALL
        SELECT
            'LearningMediaScopeTypes',
            catalog."Id",
            catalog."Key"
        FROM "LearningMediaScopeTypes" AS catalog
        UNION ALL
        SELECT
            'NotificationEventCategoryTypes',
            catalog."Id",
            catalog."Key"
        FROM "NotificationEventCategoryTypes" AS catalog
        UNION ALL
        SELECT
            'EventAudienceTypes',
            catalog."Id",
            catalog."Key"
        FROM "EventAudienceTypes" AS catalog
        UNION ALL
        SELECT
            'EventSeverityTypes',
            catalog."Id",
            catalog."Key"
        FROM "EventSeverityTypes" AS catalog
        UNION ALL
        SELECT
            'NotificationChannelTypes',
            catalog."Id",
            catalog."Key"
        FROM "NotificationChannelTypes" AS catalog
        UNION ALL
        SELECT
            'NotificationTimingTypes',
            catalog."Id",
            catalog."Key"
        FROM "NotificationTimingTypes" AS catalog
        UNION ALL
        SELECT
            'AcquisitionIndexerTypeTypes',
            catalog."Id",
            catalog."Key"
        FROM "AcquisitionIndexerTypeTypes" AS catalog
        UNION ALL
        SELECT
            'AcquisitionKindTypes',
            catalog."Id",
            catalog."Key"
        FROM "AcquisitionKindTypes" AS catalog
        UNION ALL
        SELECT
            'AcquisitionRequestStatusTypes',
            catalog."Id",
            catalog."Key"
        FROM "AcquisitionRequestStatusTypes" AS catalog
        UNION ALL
        SELECT
            'CurriculumExerciseKindTypes',
            catalog."Id",
            catalog."Key"
        FROM "CurriculumExerciseKindTypes" AS catalog
        UNION ALL
        SELECT
            'CurriculumExercisePhaseTypes',
            catalog."Id",
            catalog."Key"
        FROM "CurriculumExercisePhaseTypes" AS catalog
        UNION ALL
        SELECT
            'CurriculumObjectiveSkillTypes',
            catalog."Id",
            catalog."Key"
        FROM "CurriculumObjectiveSkillTypes" AS catalog
        UNION ALL
        SELECT
            'LearnerProgressStatusTypes',
            catalog."Id",
            catalog."Key"
        FROM "LearnerProgressStatusTypes" AS catalog
        UNION ALL
        SELECT
            'LearnerProgressSkipReasonTypes',
            catalog."Id",
            catalog."Key"
        FROM "LearnerProgressSkipReasonTypes" AS catalog
        UNION ALL
        SELECT
            'LearnerExerciseOutcomeTypes',
            catalog."Id",
            catalog."Key"
        FROM "LearnerExerciseOutcomeTypes" AS catalog
        UNION ALL
        SELECT
            'LearningActivityKindTypes',
            catalog."Id",
            catalog."Key"
        FROM "LearningActivityKindTypes" AS catalog
        UNION ALL
        SELECT
            'LearningSessionEndReasonTypes',
            catalog."Id",
            catalog."Key"
        FROM "LearningSessionEndReasonTypes" AS catalog
        UNION ALL
        SELECT
            'LearningActivityEventKindTypes',
            catalog."Id",
            catalog."Key"
        FROM "LearningActivityEventKindTypes" AS catalog
        UNION ALL
        SELECT
            'GameReleaseFileRoleTypes',
            catalog."Id",
            catalog."Key"
        FROM "GameReleaseFileRoleTypes" AS catalog
        UNION ALL
        SELECT
            'MediaSegmentSourceTypes',
            catalog."Id",
            catalog."Key"
        FROM "MediaSegmentSourceTypes" AS catalog
        UNION ALL
        SELECT
            'MediaTrackTypes',
            catalog."Id",
            catalog."Key"
        FROM "MediaTrackTypes" AS catalog
        UNION ALL
        SELECT
            'WorkRelationTypes',
            catalog."Id",
            catalog."Key"
        FROM "WorkRelationTypes" AS catalog
        UNION ALL
        SELECT
            'ImageTypes',
            catalog."Id",
            catalog."Key"
        FROM "ImageTypes" AS catalog
        UNION ALL
        SELECT
            'MediaAssetTypes',
            catalog."Id",
            catalog."Key"
        FROM "MediaAssetTypes" AS catalog
        UNION ALL
        SELECT
            'MediaDetectionTypes',
            catalog."Id",
            catalog."Key"
        FROM "MediaDetectionTypes" AS catalog
        UNION ALL
        SELECT
            'MediaDetectionStatusTypes',
            catalog."Id",
            catalog."Key"
        FROM "MediaDetectionStatusTypes" AS catalog
        UNION ALL
        SELECT
            'WorkCreditRoleTypes',
            catalog."Id",
            catalog."Key"
        FROM "WorkCreditRoleTypes" AS catalog
        UNION ALL
        SELECT
            'WorkFactTypes',
            catalog."Id",
            catalog."Key"
        FROM "WorkFactTypes" AS catalog
        UNION ALL
        SELECT
            'WorkMediaClassificationTypes',
            catalog."Id",
            catalog."Key"
        FROM "WorkMediaClassificationTypes" AS catalog
    )
    SELECT
        string_agg(discrepancy.target_table || ':' || discrepancy.id::text || ':' || discrepancy.key, ', ' ORDER BY discrepancy.target_table, discrepancy.id)
    INTO mismatch
    FROM (
        (
            SELECT
                expected.target_table,
                expected.id,
                expected.key
            FROM expected
            EXCEPT
            SELECT
                actual.target_table,
                actual.id,
                actual.key
            FROM actual
        )
        UNION ALL
        (
            SELECT
                actual.target_table,
                actual.id,
                actual.key
            FROM actual
            EXCEPT
            SELECT
                expected.target_table,
                expected.id,
                expected.key
            FROM expected
        )
    ) AS discrepancy;
    IF mismatch IS NOT NULL THEN
        RAISE EXCEPTION 'Defined target seeds mismatch: %', mismatch;
    END IF;
    IF EXISTS (
        SELECT 1 FROM "EventAudienceTypes" AS audience
        WHERE audience."RequiresProfile" IS DISTINCT FROM (audience."Id" = 1)
    ) OR EXISTS (
        SELECT 1 FROM "NotificationEventCategoryTypes" AS category
        WHERE (category."EventAudienceTypeId", category."EventSeverityTypeId") IS DISTINCT FROM
            (CASE WHEN category."Id" = 8 THEN 2 ELSE 1 END,
             CASE WHEN category."Id" = 8 THEN 3 WHEN category."Id" IN (2, 4) THEN 2 ELSE 1 END)
    ) THEN
        RAISE EXCEPTION 'Seeded event audience or category policy mismatch';
    END IF;
END $target_seed_contract$;
ROLLBACK;

BEGIN;
DO $rule_type_integrity$
DECLARE
    quality_profile_id bigint;
    rule_id bigint;
    rejected_constraint text;
BEGIN
    INSERT INTO "AcquisitionQualityProfiles" ("Key", "Name")
    VALUES ('phase-b-typed-rule', 'Typed rule')
    RETURNING "Id" INTO quality_profile_id;
    INSERT INTO "AcquisitionQualityProfileRules" (
        "AcquisitionQualityProfileId", "Ordinal", "Name", "AcquisitionRuleFieldTypeId",
        "AcquisitionRuleMatchTypeId", "AcquisitionRuleEffectTypeId", "MatchValue")
    VALUES (quality_profile_id, 0, 'Raw title preference', 0, 0, 0, 'Allowed title')
    RETURNING "Id" INTO rule_id;

    BEGIN
        UPDATE "AcquisitionQualityProfileRules"
        SET "AcquisitionRuleFieldTypeId" = 254
        WHERE "Id" = rule_id;
        RAISE EXCEPTION 'Rule accepted an unknown field type';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'FK_AcquisitionQualityProfileRules_Fields' THEN
            RAISE;
        END IF;
    END;
    BEGIN
        UPDATE "AcquisitionQualityProfileRules"
        SET "AcquisitionRuleMatchTypeId" = 254
        WHERE "Id" = rule_id;
        RAISE EXCEPTION 'Rule accepted an unknown match type';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'FK_AcquisitionQualityProfileRules_Matches' THEN
            RAISE;
        END IF;
    END;
    BEGIN
        UPDATE "AcquisitionQualityProfileRules"
        SET "AcquisitionRuleEffectTypeId" = 254
        WHERE "Id" = rule_id;
        RAISE EXCEPTION 'Rule accepted an unknown effect type';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'FK_AcquisitionQualityProfileRules_Effects' THEN
            RAISE;
        END IF;
    END;
END $rule_type_integrity$;
ROLLBACK;
