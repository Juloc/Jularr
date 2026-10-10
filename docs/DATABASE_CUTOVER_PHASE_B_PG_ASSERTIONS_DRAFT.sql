-- READ-ONLY STRUCTURAL ASSERTIONS FOR FRESH, ISOLATED PHASE-B TARGET DATABASE ONLY.
-- Execute after DRAFTS 01..09, NEVER on the current dev or production DB.
-- This checks actual PostgreSQL catalog constraints, NOT merely SQL text.
-- It does NOT substitute insertion rejection tests or an EXPLAIN/EF baseline.
BEGIN TRANSACTION READ ONLY;
DO $phase_b$
DECLARE
    missing text;
BEGIN
    WITH required_constraint(name, kind) AS (
        VALUES
        ('FK_Profiles_OwnerAccountProfiles','f'),
        ('FK_MediaAssets_WorkVersions','f'),
        ('FK_StoredFiles_MediaAssets','f'),
        ('FK_GameReleaseStoredFiles_StoredFiles','f'),
        ('FK_PlaybackSessions_MediaAssets','f'),
        ('FK_PlaybackSessions_StoredFiles','f'),
        ('FK_MediaPlaybackHistory_MediaAssets','f'),
        ('FK_LearningActivitySessions_LearnerCourses','f'),
        ('FK_LearningActivityEvents_LearningActivitySessions','f'),
        ('FK_LearningCardReviews_LearningCards','f'),
        ('FK_LearnerExerciseAttempts_CurriculumExercises','f'),
        ('FK_MediaProgress_TimeProgressPositions','f'),
        ('FK_MediaProgress_ReadingProgressPositions','f'),
        ('FK_MediaProgress_GameProgressPositions','f'),
        ('CK_ImageAssignments_OneTarget','c'),
        ('CK_ImageAssignments_ValidKind','c'),
        ('CK_MediaProgress_TargetCount','c'),
        ('UX_LearningActivityEvents_Profile_Event_Kind','u')
    )
    SELECT string_agg(r.name,', ' ORDER BY r.name)
    INTO missing
    FROM required_constraint AS r
    LEFT JOIN pg_constraint AS c ON c.conname = r.name AND c.contype::text = r.kind
    WHERE c.oid IS NULL;
    IF missing IS NOT NULL THEN
        RAISE EXCEPTION 'Missing required target constraints: %', missing;
    END IF;

    IF (
        SELECT count(*)
        FROM pg_constraint
        WHERE conname IN (
            'FK_MediaProgress_TimeProgressPositions',
            'FK_MediaProgress_ReadingProgressPositions',
            'FK_MediaProgress_GameProgressPositions',
            'FK_Profiles_OwnerAccountProfiles'
        ) AND condeferrable AND condeferred
    ) <> 4 THEN
        RAISE EXCEPTION 'Progress total subtype and profile-owner cyclic FKs must be DEFERRABLE INITIALLY DEFERRED';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM (VALUES
            ('TimePositionId'),('ReadingPositionId'),('GamePositionId')
        ) AS expected(col)
        LEFT JOIN pg_attribute AS a
            ON a.attrelid = '"MediaProgress"'::regclass
            AND a.attname = expected.col
            AND a.attgenerated = 's'
        WHERE a.attnum IS NULL
    ) THEN
        RAISE EXCEPTION 'MediaProgress total-subtype generated columns are missing';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_attribute AS a
        WHERE a.attrelid='"Accounts"'::regclass AND a.attname='Email'
          AND format_type(a.atttypid,a.atttypmod)='citext'
          AND a.attnotnull
    ) THEN
        RAISE EXCEPTION 'Accounts.Email must be required PostgreSQL citext';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_index AS i
        WHERE i.indrelid='"MediaProgress"'::regclass
          AND i.indisunique AND i.indnullsnotdistinct
    ) THEN
        RAISE EXCEPTION 'MediaProgress needs its exact NULLS NOT DISTINCT uniqueness index';
    END IF;

    IF (
        SELECT count(*) FROM pg_class AS t JOIN pg_namespace AS n ON n.oid=t.relnamespace
        WHERE t.relkind IN ('r','p') AND n.nspname=current_schema()
          AND t.relname IN ('Works','GameReleases','MediaAssets','StoredFiles',
              'Profiles','AccountProfiles','MediaProgress','LearningActivityEvents')
    ) <> 8 THEN
        RAISE EXCEPTION 'Core target tables missing from current_schema()';
    END IF;

    RAISE NOTICE 'Phase B structural invariants present. Next run actual rejection/rollback/integration tests.';
END $phase_b$;
ROLLBACK;
