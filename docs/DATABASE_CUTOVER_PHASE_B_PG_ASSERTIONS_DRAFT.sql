-- READ-ONLY STRUCTURAL ASSERTIONS FOR FRESH, ISOLATED PHASE-B TARGET DATABASE ONLY.
-- Execute after the current cutover DDL set, NEVER on the current dev or production DB.
-- This checks actual PostgreSQL catalog constraints, NOT merely SQL text.
-- It does NOT substitute insertion rejection tests or an EXPLAIN/EF baseline.
BEGIN TRANSACTION READ ONLY;
DO $phase_b$
DECLARE
    missing text;
BEGIN
    IF EXISTS (
        SELECT 1
        FROM pg_class AS relation
        JOIN pg_namespace AS schema ON schema.oid = relation.relnamespace
        WHERE schema.nspname = current_schema()
          AND relation.relkind IN ('r','p')
          AND (relation.relname LIKE ANY (ARRAY['Learning%', 'Learner%', 'Curriculum%', 'SharedCourse%'])
               OR relation.relname = 'Terms')
    ) OR EXISTS (
        SELECT 1 FROM pg_attribute AS attribute
        WHERE attribute.attrelid = '"Profiles"'::regclass
          AND attribute.attname = 'IsLearningEnabled' AND NOT attribute.attisdropped
    ) THEN
        RAISE EXCEPTION 'Deferred Learning must not be part of the cutover schema';
    END IF;

    WITH required_constraint(name, kind) AS (
        VALUES
        ('FK_Profiles_OwnerAccountProfiles','f'),
        ('FK_MediaAssets_WorkVersions','f'),
        ('FK_StoredFiles_MediaAssets','f'),
        ('FK_GameReleaseStoredFiles_StoredFiles','f'),
        ('FK_PlaybackSessions_MediaAssets','f'),
        ('FK_PlaybackSessions_StoredFiles','f'),
        ('FK_MediaPlaybackHistory_MediaAssets','f'),
        ('FK_MediaProgress_TimeProgressPositions','f'),
        ('FK_MediaProgress_ReadingProgressPositions','f'),
        ('FK_MediaProgress_GameProgressPositions','f'),
        ('FK_ReadingProgressPositions_ProgressWork','f'),
        ('FK_ReadingProgressPositions_ReaderContent','f'),
        ('FK_ReaderContent_WorkEditions','f'),
        ('CK_ImageAssignments_OneTarget','c'),
        ('CK_ImageAssignments_ValidKind','c'),
        ('CK_MediaProgress_TargetCount','c'),
        ('FK_Events_EventAudienceTypes','f'),
        ('FK_Notifications_EventsAudience','f'),
        ('FK_NotificationDeliveries_EventsAudience','f'),
        ('FK_NotificationDeliveryAttempts_NotificationDeliveries','f'),
        ('CK_Notifications_ExactlyOneRecipient','c'),
        ('CK_NotificationDeliveries_ExactlyOneRecipient','c'),
        ('UX_Notifications_Event_Recipient','u'),
        ('UX_Notifications_Group_Recipient','u'),
        ('FK_Notifications_EventGroup','f'),
        ('FK_NotificationEvents_InboxAudience','f'),
        ('FK_NotificationEvents_EventProfile','f'),
        ('PK_NotificationEvents','p'),
        ('UX_NotificationDeliveries_Occurrence_Route','u'),
        ('FK_NotificationDeliveries_EndpointRecipient','f'),
        ('FK_NotificationExternalOccurrences_DigestPolicy','f'),
        ('FK_NotificationDeliveries_ImmediateOccurrence','f'),
        ('PK_NotificationExternalOccurrences','p'),
        ('FK_AcquisitionDownloadWantedItems_BindingWork','f'),
        ('FK_AcquisitionDownloadWantedItems_TargetWork','f'),
        ('FK_WorkTitles_Providers','f'),
        ('CK_AccountPasskeys_BackupState','c'),
        ('CK_WorkMetadataFacts_CommunityRating','c'),
        ('FK_MusicArtists_People','f')
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
        ) AND condeferrable AND condeferred AND confdeltype = 'a'
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

    -- Existing wanted target uniqueness is implemented as a unique index,
    -- not pg_constraint; assert its true null-equality semantics explicitly.
    IF NOT EXISTS (
        SELECT 1
        FROM pg_index AS ix
        JOIN pg_class AS i ON i.oid = ix.indexrelid
        WHERE ix.indrelid = '"WantedItems"'::regclass
          AND i.relname = 'UX_WantedItems_ExactTarget'
          AND ix.indisunique AND ix.indnullsnotdistinct
    ) THEN
        RAISE EXCEPTION 'WantedItems exact-target NULLS NOT DISTINCT unique index is missing';
    END IF;

    IF (
        SELECT count(*) FROM pg_class AS t JOIN pg_namespace AS n ON n.oid=t.relnamespace
        WHERE t.relkind IN ('r','p') AND n.nspname=current_schema()
          AND t.relname IN ('Works','GameReleases','MediaAssets','StoredFiles',
              'Profiles','AccountProfiles','MediaProgress')
    ) <> 7 THEN
        RAISE EXCEPTION 'Core target tables missing from current_schema()';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM (VALUES
            ('AcquisitionIndexers','Id'),
            ('AcquisitionDownloadClients','Id'),
            ('AcquisitionDownloadBindings','AcquisitionDownloadClientId')
        ) AS expected(table_name,column_name)
        WHERE NOT EXISTS (
            SELECT 1
            FROM pg_attribute AS a
            JOIN pg_class AS t ON t.oid = a.attrelid
            WHERE t.relname = expected.table_name
              AND a.attname = expected.column_name
              AND a.atttypid = 'bigint'::regtype
              AND NOT a.attisdropped
        )
    ) THEN
        RAISE EXCEPTION 'Canonical internal acquisition IDs must be bigint';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_attribute AS a
        WHERE a.attrelid = '"AccountPasskeys"'::regclass
          AND a.attname = 'Transports' AND a.atttypid = 'text[]'::regtype
    ) THEN
        RAISE EXCEPTION 'Passkey transports must be a typed PostgreSQL text array';
    END IF;

        RAISE NOTICE 'Phase B structural invariants present. Next run actual rejection/rollback/integration tests.';
END $phase_b$;
ROLLBACK;
