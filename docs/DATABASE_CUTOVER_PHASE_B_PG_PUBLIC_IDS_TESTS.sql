-- Phase B B-ID: public UUIDs and internal bigint may not be conflated.
-- Disposable phase_b_scratch ONLY. Never infer effective permission from PublicId.
BEGIN;
DO $public_id_contract$
DECLARE
    source_table text;
    first_public_id uuid;
    second_public_id uuid;
    first_internal_id bigint;
BEGIN
    FOR source_table IN SELECT item.name FROM (VALUES
            ('Accounts'),
            ('AccountGroups'),
            ('Profiles'),
            ('Works'),
            ('WorkEpisodes'),
            ('WorkChapters'),
            ('WorkTracks'),
            ('WorkEditions'),
            ('MediaProgress'),
            ('Images'),
            ('Collections'),
            ('AcquisitionRequests'),
            ('Operations'),
            ('PlaybackSessions'),
            ('ReaderBookmarks'),
            ('ReaderHighlights'),
            ('Notifications'),
            ('NotificationPushEndpoints'),
            ('LibraryRoots')
        ) AS item(name)
    LOOP
        IF NOT EXISTS (
            SELECT 1
            FROM pg_attribute AS attr
            JOIN pg_class AS relation ON relation.oid = attr.attrelid
            WHERE relation.oid = format('%I',source_table)::regclass
              AND attr.attname = 'PublicId'
              AND attr.atttypid = 'uuid'::regtype
              AND attr.attnotnull
        ) THEN
            RAISE EXCEPTION 'Missing non-null UUID PublicId on %',source_table;
        END IF;
        IF NOT EXISTS (
            SELECT 1
            FROM pg_constraint AS con
            WHERE con.conrelid=format('%I',source_table)::regclass
              AND con.conname='UX_' || source_table || '_PublicId'
              AND con.contype='u'
        ) THEN
            RAISE EXCEPTION 'Missing unique UUID public ID on %',source_table;
        END IF;
        IF EXISTS (
            SELECT 1
            FROM pg_constraint AS con
            WHERE con.contype='f'
              AND con.confrelid=format('%I',source_table)::regclass
              AND con.confkey @> ARRAY[
                  (SELECT attnum FROM pg_attribute
                   WHERE attrelid=con.confrelid AND attname='PublicId')
              ]::smallint[]
        ) THEN
            RAISE EXCEPTION 'Relational FK illegally uses PublicId of %',source_table;
        END IF;
    END LOOP;

    IF NOT EXISTS (SELECT 1 FROM "MediaTypes" WHERE "Id"=1) THEN
       RAISE EXCEPTION 'Test MediaType id=1 fixture absent';
    END IF;

    INSERT INTO "Works" ("MediaTypeId","CanonicalTitle")
    VALUES (1,'Phase B public work A')
    RETURNING "Id","PublicId" INTO first_internal_id,first_public_id;
    INSERT INTO "Works" ("MediaTypeId","CanonicalTitle")
    VALUES (1,'Phase B public work B')
    RETURNING "PublicId" INTO second_public_id;
    IF first_public_id IS NULL OR second_public_id IS NULL OR
       first_public_id=second_public_id OR first_internal_id IS NULL THEN
       RAISE EXCEPTION 'Public UUID is not distinct from internal bigint identity';
    END IF;
    BEGIN
       INSERT INTO "Works" ("MediaTypeId","CanonicalTitle","PublicId")
       VALUES (1,'Phase B duplicate public id',first_public_id);
       RAISE EXCEPTION 'Duplicate public UUID was accepted';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    BEGIN
       INSERT INTO "Works" ("MediaTypeId","CanonicalTitle","PublicId")
       VALUES (1,'Phase B missing public id',NULL);
       RAISE EXCEPTION 'Null public UUID was accepted';
    EXCEPTION WHEN not_null_violation THEN NULL;
    END;
    RAISE NOTICE 'Separate opaque PublicId vs internal bigint and uniqueness validated';
END $public_id_contract$;
ROLLBACK;
