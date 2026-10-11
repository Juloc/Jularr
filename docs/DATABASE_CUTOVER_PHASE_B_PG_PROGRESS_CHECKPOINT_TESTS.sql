-- Canonical static writer, not a separate test implementation. Disposable PG only.
\set ON_ERROR_STOP on
\i /tmp/phase_b_progress_checkpoint_prepared.sql
\i /tmp/phase_b_progress_receipt_prepared.sql
\i /tmp/phase_b_progress_start_prepared.sql
\i /tmp/phase_b_progress_target_prepared.sql
\i /tmp/phase_b_progress_snapshot_prepared.sql
BEGIN;
INSERT INTO "Operations" ("OperationKindKey", "OperationStatusTypeId", "ClaimedBy", "ClaimedUntil")
VALUES ('reader_parse', 2, 'reader-fixture', now() + INTERVAL '1 hour') RETURNING "Id" AS reader_operation \gset
INSERT INTO "UiLocales" ("Locale", "Name") VALUES ('checkpoint-test', 'Checkpoint test')
RETURNING "Id" AS locale_id \gset
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('checkpoint-a@example.invalid', 'Checkpoint A', 2) RETURNING "Id" AS actor_a \gset
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('checkpoint-b@example.invalid', 'Checkpoint B', 2) RETURNING "Id" AS actor_b \gset
INSERT INTO "Profiles" ("OwnerAccountId", "DisplayName", "UiLocaleId")
VALUES (:actor_a, 'Checkpoint A', :locale_id) RETURNING "Id" AS profile_id \gset
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (:actor_a, :profile_id);
INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (3, 'Checkpoint reader')
RETURNING "Id" AS reader_work, "PublicId" AS reader_work_public \gset
INSERT INTO "WorkEditions" ("WorkId", "Name") VALUES (:reader_work, 'Exact edition')
RETURNING "Id" AS edition_id, "PublicId" AS edition_public \gset
INSERT INTO "WorkEditions" ("WorkId", "Name") VALUES (:reader_work, 'Other edition')
RETURNING "Id" AS other_edition_id, "PublicId" AS other_edition_public \gset
INSERT INTO "ReaderContent" ("WorkId", "WorkEditionId", "ContentRevision", "StorageKey", "ReaderLayoutTypeId",
    "ContentHash", "ParserVersion", "BuilderOperationId", "NodeCount", "PageCount")
VALUES (:reader_work, :edition_id, 1, 'checkpoint-reader-1', 2, decode(repeat('a1',32),'hex'), 'fixture', :reader_operation, 0, 10)
RETURNING "Id" AS content_1 \gset
INSERT INTO "ReaderContent" ("WorkId", "WorkEditionId", "ContentRevision", "StorageKey", "ReaderLayoutTypeId",
    "ContentHash", "ParserVersion", "BuilderOperationId", "NodeCount", "PageCount")
VALUES (:reader_work, :edition_id, 2, 'checkpoint-reader-2', 2, decode(repeat('a2',32),'hex'), 'fixture', :reader_operation, 0, 5)
RETURNING "Id" AS content_2 \gset
INSERT INTO "ReaderContent" ("WorkId", "WorkEditionId", "ContentRevision", "StorageKey", "ReaderLayoutTypeId",
    "ContentHash", "ParserVersion", "BuilderOperationId", "NodeCount", "PageCount")
VALUES (:reader_work, :edition_id, 3, 'checkpoint-reader-3', 1, decode(repeat('a3',32),'hex'), 'fixture', :reader_operation, 1, 0)
RETURNING "Id" AS content_3 \gset
INSERT INTO "ReaderContent" ("WorkId", "WorkEditionId", "ContentRevision", "StorageKey", "ReaderLayoutTypeId",
    "ContentHash", "ParserVersion", "BuilderOperationId", "NodeCount", "PageCount")
VALUES (:reader_work, :other_edition_id, 1, 'checkpoint-reader-other', 2, decode(repeat('a4',32),'hex'), 'fixture', :reader_operation, 0, 10)
RETURNING "Id" AS other_content \gset
INSERT INTO "ReaderPages" ("ReaderContentId", "PageIndex")
SELECT :content_1, i FROM generate_series(0, 9) AS i;
INSERT INTO "ReaderPages" ("ReaderContentId", "PageIndex")
SELECT :content_2, i FROM generate_series(0, 4) AS i;
INSERT INTO "ReaderPages" ("ReaderContentId", "PageIndex") SELECT :other_content, i FROM generate_series(0,9) AS i;
INSERT INTO "ReaderContentNodes" ("ReaderContentId", "StableId", "OrderIndex", "ReaderNodeTypeId", "ReaderTextBlockTypeId", "CharacterCount")
VALUES (:content_3, 'chapter-1:p1', 0, 1, 1, 100);
UPDATE "ReaderContent" SET "PublishedAt"=now() WHERE "Id" IN (:content_1,:content_2,:content_3,:other_content);
EXECUTE phase_b_progress_start(:actor_a,:profile_id,:'reader_work_public',NULL,NULL,NULL,:'edition_public',2,now()) \gset
SELECT "Id" AS reader_id, "PublicId" AS reader_public FROM "MediaProgress" WHERE "PublicId"=:'MediaProgressId' \gset
INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (1, 'Checkpoint time')
RETURNING "Id" AS time_work, "PublicId" AS time_work_public \gset
EXECUTE phase_b_progress_start(:actor_a,:profile_id,:'time_work_public',NULL,NULL,NULL,:'edition_public',1,now());
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_start(:actor_a,:profile_id,:'time_work_public',NULL,NULL,NULL,NULL,1,now()) \gset
SELECT "Id" AS time_id, "PublicId" AS time_public FROM "MediaProgress" WHERE "PublicId"=:'MediaProgressId' \gset
EXECUTE phase_b_progress_start(:actor_a,:profile_id,:'time_work_public',NULL,NULL,NULL,NULL,1,now());
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_target(:actor_a,:profile_id,:'time_work_public',NULL,NULL,NULL,NULL,1) \gset
SELECT 1 / CASE WHEN :'MediaProgressId'::uuid=:'time_public'::uuid AND :Revision=1 THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_target(:actor_b,:profile_id,:'time_work_public',NULL,NULL,NULL,NULL,1);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (7, 'Checkpoint game')
RETURNING "Id" AS game_work, "PublicId" AS game_work_public \gset
EXECUTE phase_b_progress_start(:actor_a,:profile_id,:'game_work_public',NULL,NULL,NULL,NULL,3,now()) \gset
SELECT "Id" AS game_id, "PublicId" AS game_public FROM "MediaProgress" WHERE "PublicId"=:'MediaProgressId' \gset
COMMIT;

-- Position beyond duration is not proof of natural completion.
EXECUTE phase_b_progress_checkpoint(:actor_a,:profile_id,:'time_public',1,gen_random_uuid(),decode(repeat('11',32),'hex'),1,now(),NULL,NULL,false,20000,10000,NULL,NULL,NULL,NULL,NULL,NULL,NULL) \gset
SELECT 1 / CASE WHEN :Revision = 2 AND NOT :'IsCompleted'::boolean THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_checkpoint(:actor_a,:profile_id,:'time_public',2,gen_random_uuid(),decode(repeat('12',32),'hex'),1,now(),NULL,1,false,10000,10000,NULL,NULL,NULL,NULL,NULL,NULL,NULL) \gset
SELECT 1 / CASE WHEN :Revision = 3 AND :'IsCompleted'::boolean THEN 1 ELSE 0 END;
SELECT "CompletedAt" AS original_completed_at FROM "MediaProgress" WHERE "Id"=:time_id \gset
EXECUTE phase_b_progress_checkpoint(:actor_a,:profile_id,:'time_public',3,gen_random_uuid(),decode(repeat('13',32),'hex'),2,now(),now()+INTERVAL '1 year',NULL,false,0,10000,NULL,NULL,NULL,NULL,NULL,NULL,NULL) \gset
SELECT 1 / CASE WHEN :Revision = 4 AND :'IsCompleted'::boolean
    AND (SELECT "CompletedAt"=:'original_completed_at'::timestamptz AND "CompletionReasonTypeId"=1
        FROM "MediaProgress" WHERE "Id"=:time_id) THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_checkpoint(:actor_a,:profile_id,:'time_public',4,gen_random_uuid(),decode(repeat('14',32),'hex'),2,now(),NULL,NULL,true,0,10000,NULL,NULL,NULL,NULL,NULL,NULL,NULL);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_checkpoint(:actor_a,:profile_id,:'time_public',4,gen_random_uuid(),decode(repeat('14',32),'hex'),3,now(),NULL,NULL,true,0,10000,NULL,NULL,NULL,NULL,NULL,NULL,NULL) \gset
SELECT 1 / CASE WHEN :Revision=5 AND NOT :'IsCompleted'::boolean THEN 1 ELSE 0 END;

-- A current revision and a stable event ID jointly protect the exact content anchor.
SELECT gen_random_uuid() AS reader_event \gset
EXECUTE phase_b_progress_checkpoint(:actor_a,:profile_id,:'reader_public',1,:'reader_event',decode(repeat('21',32),'hex'),2,now(),NULL,NULL,false,NULL,NULL,:'edition_public',1,'original','{"pageIndex":8}',1000,NULL,NULL) \gset
SELECT 1 / CASE WHEN :Revision=2 AND NOT :'IsCompleted'::boolean THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_checkpoint(:actor_a,:profile_id,:'reader_public',2,:'reader_event',decode(repeat('21',32),'hex'),2,now(),NULL,NULL,false,NULL,NULL,:'edition_public',1,'original','{"pageIndex":8}',1000,NULL,NULL);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_receipt(:actor_a,:profile_id,:'reader_event',:'reader_public',decode(repeat('21',32),'hex')) \gset
SELECT 1 / CASE WHEN :AcceptedRevision=2 AND :CurrentRevision=2 THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_receipt(:actor_a,:profile_id,:'reader_event',:'reader_public',decode(repeat('22',32),'hex'));
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_receipt(:actor_b,:profile_id,:'reader_event',:'reader_public',decode(repeat('21',32),'hex'));
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_checkpoint(:actor_a,:profile_id,:'reader_public',2,gen_random_uuid(),decode(repeat('23',32),'hex'),1,now(),NULL,NULL,false,NULL,NULL,:'other_edition_public',1,'original','{"pageIndex":8}',1000,NULL,NULL);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_checkpoint(:actor_a,:profile_id,:'reader_public',2,gen_random_uuid(),decode(repeat('23',32),'hex'),1,now(),NULL,NULL,false,NULL,NULL,:'edition_public',99,'original','{"pageIndex":8}',1000,NULL,NULL);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_checkpoint(:actor_a,:profile_id,:'reader_public',2,gen_random_uuid(),decode(repeat('23',32),'hex'),1,now(),NULL,NULL,false,NULL,NULL,:'edition_public',2,'original','{"pageIndex":8}',1000,NULL,NULL);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
SELECT 1 / CASE WHEN (SELECT "ReaderContentId"=:content_1 AND "PageIndex"=8 FROM "ReadingProgressPositions" WHERE "MediaProgressId"=:reader_id) THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_checkpoint(:actor_a,:profile_id,:'reader_public',2,gen_random_uuid(),decode(repeat('24',32),'hex'),1,now(),NULL,NULL,false,NULL,NULL,:'edition_public',2,'original','{"pageIndex":2}',1000,NULL,NULL) \gset
SELECT 1 / CASE WHEN :Revision=3 AND NOT :'IsCompleted'::boolean THEN 1 ELSE 0 END;
SELECT 1 / CASE WHEN (SELECT "ReaderContentId"=:content_2 AND "PageIndex"=2 FROM "ReadingProgressPositions" WHERE "MediaProgressId"=:reader_id) THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_checkpoint(:actor_a,:profile_id,:'reader_public',3,gen_random_uuid(),decode(repeat('25',32),'hex'),1,now(),NULL,2,false,NULL,NULL,:'edition_public',3,'original','{"blockId":"chapter-1:p1","characterOffset":42}',1000,NULL,NULL) \gset
SELECT 1 / CASE WHEN :Revision=4 AND :'IsCompleted'::boolean THEN 1 ELSE 0 END;

-- All three mutations roll back together, including completion provenance.
SELECT gen_random_uuid() AS rolled_back_event \gset
BEGIN;
EXECUTE phase_b_progress_checkpoint(:actor_a,:profile_id,:'reader_public',4,:'rolled_back_event',decode(repeat('26',32),'hex'),3,now(),NULL,NULL,true,NULL,NULL,:'edition_public',3,'original','{"blockId":"chapter-1:p1","characterOffset":40}',900,NULL,NULL) \gset
SELECT 1 / CASE WHEN :Revision=5 AND NOT :'IsCompleted'::boolean THEN 1 ELSE 0 END;
ROLLBACK;
SELECT 1 / CASE WHEN (SELECT "Revision"=4 AND "IsCompleted" FROM "MediaProgress" WHERE "Id"=:reader_id)
    AND NOT EXISTS (SELECT 1 FROM "MediaProgressCheckpointEvents" WHERE "ClientEventId"=:'rolled_back_event')
    AND (SELECT "PageIndex" IS NULL AND "Locator"->>'characterOffset'='42'
        FROM "ReadingProgressPositions" WHERE "MediaProgressId"=:reader_id) THEN 1 ELSE 0 END;

EXECUTE phase_b_progress_checkpoint(:actor_a,:profile_id,:'game_public',1,gen_random_uuid(),decode(repeat('31',32),'hex'),1,now(),NULL,NULL,false,NULL,NULL,NULL,NULL,NULL,NULL,NULL,600000,100) \gset
SELECT 1 / CASE WHEN :Revision=2 AND NOT :'IsCompleted'::boolean THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_checkpoint(:actor_a,:profile_id,:'game_public',2,gen_random_uuid(),decode(repeat('32',32),'hex'),3,now(),NULL,3,false,NULL,NULL,NULL,NULL,NULL,NULL,NULL,600000,100) \gset
SELECT 1 / CASE WHEN :Revision=3 AND :'IsCompleted'::boolean THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_checkpoint(:actor_a,:profile_id,:'game_public',3,gen_random_uuid(),decode(repeat('33',32),'hex'),1,now(),NULL,1,false,NULL,NULL,NULL,NULL,NULL,NULL,NULL,600000,100);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;

EXECUTE phase_b_progress_checkpoint(:actor_b,:profile_id,:'time_public',5,gen_random_uuid(),decode(repeat('41',32),'hex'),1,now(),NULL,NULL,false,5000,10000,NULL,NULL,NULL,NULL,NULL,NULL,NULL);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
BEGIN;
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (:actor_b,:profile_id);
EXECUTE phase_b_progress_checkpoint(:actor_b,:profile_id,:'time_public',5,gen_random_uuid(),decode(repeat('41',32),'hex'),1,now(),NULL,NULL,false,5000,10000,NULL,NULL,NULL,NULL,NULL,NULL,NULL) \gset
SELECT 1 / CASE WHEN :Revision=6 THEN 1 ELSE 0 END;
DELETE FROM "AccountProfiles" WHERE "AccountId"=:actor_b AND "ProfileId"=:profile_id;
EXECUTE phase_b_progress_checkpoint(:actor_b,:profile_id,:'time_public',6,gen_random_uuid(),decode(repeat('42',32),'hex'),1,now(),NULL,NULL,false,5000,10000,NULL,NULL,NULL,NULL,NULL,NULL,NULL);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
UPDATE "Accounts" SET "IsEnabled"=false WHERE "Id"=:actor_a;
EXECUTE phase_b_progress_checkpoint(:actor_a,:profile_id,:'time_public',6,gen_random_uuid(),decode(repeat('42',32),'hex'),1,now(),NULL,NULL,false,5000,10000,NULL,NULL,NULL,NULL,NULL,NULL,NULL);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
ROLLBACK;

EXECUTE phase_b_progress_snapshot(:actor_a,:profile_id,:'reader_public') \gset
SELECT 1 / CASE WHEN :Revision=4 AND :'IsCompleted'::boolean AND :CompletionReasonTypeId=2
    AND :'ReaderEditionId'::uuid=:'edition_public'::uuid AND :ContentRevision=3
    AND :'VariantKey'='original' AND :'Locator'::jsonb->>'characterOffset'='42' THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_snapshot(:actor_a,:profile_id,:'time_public') \gset
SELECT 1 / CASE WHEN :Revision=5 AND NOT :'IsCompleted'::boolean AND :PositionMs=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_snapshot(:actor_a,:profile_id,:'game_public') \gset
SELECT 1 / CASE WHEN :Revision=3 AND :'IsCompleted'::boolean AND :PlayedMs=600000 THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_snapshot(:actor_b,:profile_id,:'reader_public');
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;

BEGIN;
SAVEPOINT malformed_checkpoint;
\set ON_ERROR_STOP off
EXECUTE phase_b_progress_checkpoint(:actor_a,:profile_id,:'reader_public',4,gen_random_uuid(),decode(repeat('aa',31),'hex'),3,now(),NULL,NULL,true,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL);
\set rejected_state :SQLSTATE
\set rejected_message :LAST_ERROR_MESSAGE
\set ON_ERROR_STOP on
ROLLBACK TO SAVEPOINT malformed_checkpoint;
SELECT 1 / CASE WHEN :'rejected_state'='23514'
    AND strpos(:'rejected_message','CK_MediaProgressCheckpointEvents_Hash')>0 THEN 1 ELSE 0 END;
COMMIT;

DO $constraints$
DECLARE
    rejected text;
    reader_id bigint;
    content_id bigint;
    other_edition bigint;
    actor_id bigint;
    profile_id bigint;
    receipt_count bigint;
BEGIN
    SELECT progress."Id", position."ReaderContentId" INTO reader_id, content_id
    FROM "MediaProgress" AS progress
    INNER JOIN "Works" AS work ON work."Id"=progress."WorkId"
    INNER JOIN "ReadingProgressPositions" AS position ON position."MediaProgressId"=progress."Id"
    WHERE work."CanonicalTitle"='Checkpoint reader';
    SELECT edition."Id" INTO other_edition FROM "WorkEditions" AS edition WHERE edition."Name"='Other edition';
    SELECT progress."ProfileId", profile."OwnerAccountId"
    INTO profile_id, actor_id
    FROM "MediaProgress" AS progress INNER JOIN "Profiles" AS profile ON profile."Id"=progress."ProfileId"
    WHERE progress."Id"=reader_id;
    SELECT count(*) INTO receipt_count FROM "MediaProgressCheckpointEvents" WHERE "MediaProgressId"=reader_id;
    BEGIN
        INSERT INTO "MediaProgressCheckpointEvents" (
            "ProfileId", "MediaProgressId", "ClientEventId", "AcceptedRevision", "ActorAccountId",
            "ProgressCheckpointSourceTypeId", "PayloadHash", "CompletionReasonTypeId")
        VALUES (profile_id, reader_id, gen_random_uuid(), 999, actor_id, 1, decode(repeat('aa',32),'hex'), 1);
        RAISE EXCEPTION 'Reader receipt accepted playback-ended reason';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected = CONSTRAINT_NAME;
        IF rejected <> 'FK_MediaProgressCheckpointEvents_CompletionPosition' THEN RAISE; END IF;
    END;
    IF (SELECT "Revision" FROM "MediaProgress" WHERE "Id"=reader_id) <> 4
        OR (SELECT count(*) FROM "MediaProgressCheckpointEvents" WHERE "MediaProgressId"=reader_id) <> receipt_count
        OR NOT (SELECT "IsCompleted" FROM "MediaProgress" WHERE "Id"=reader_id) THEN
        RAISE EXCEPTION 'Rejected checkpoint left partial state';
    END IF;
    BEGIN
        UPDATE "ReaderContent" SET "StorageKey"='replacement-bytes' WHERE "Id"=content_id;
        RAISE EXCEPTION 'Reader revision was mutated';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected = CONSTRAINT_NAME;
        IF rejected <> 'CK_ReaderContent_ImmutableRevision' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "MediaProgress" SET "WorkEditionId"=other_edition WHERE "Id"=reader_id;
        SET CONSTRAINTS ALL IMMEDIATE;
        RAISE EXCEPTION 'Progress silently changed anchor edition';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected = CONSTRAINT_NAME;
        IF rejected <> 'CK_ReadingProgressPositions_ExactTarget' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "ReadingProgressPositions" SET "ReaderContentId"=NULL WHERE "MediaProgressId"=reader_id;
        RAISE EXCEPTION 'Detached locator was accepted';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected = CONSTRAINT_NAME;
        IF rejected <> 'CK_ReadingProgressPositions_Anchor' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "ReadingProgressPositions" SET "Locator"='{"pageIndex":999}' WHERE "MediaProgressId"=reader_id;
        RAISE EXCEPTION 'Nonexistent source page was accepted';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected = CONSTRAINT_NAME;
        IF rejected <> 'CK_ReaderAnchor_ExactLocator' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "MediaProgress" SET "IsCompleted"=false WHERE "Id"=reader_id;
        RAISE EXCEPTION 'Incomplete state retained completion provenance';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected = CONSTRAINT_NAME;
        IF rejected <> 'CK_MediaProgress_Completion' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "MediaProgressCheckpointEvents" SET "PayloadHash"=decode(repeat('ff',32),'hex')
        WHERE "MediaProgressId"=reader_id;
        RAISE EXCEPTION 'Receipt was mutated';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected = CONSTRAINT_NAME;
        IF rejected <> 'CK_MediaProgressCheckpointEvents_Immutable' THEN RAISE; END IF;
    END;
END $constraints$;
\i /tmp/phase_b_continue_prepared.sql
BEGIN;
INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle")
SELECT 1, 'Checkpoint page fixture ' || i FROM generate_series(1,1000) AS i;
INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "ProgressPositionTypeId", "LastActivityAt")
SELECT :profile_id, work."Id", 1, now() FROM "Works" AS work
WHERE work."CanonicalTitle" LIKE 'Checkpoint page fixture %';
INSERT INTO "TimeProgressPositions" ("MediaProgressId", "PositionMs")
SELECT progress."Id", 12345 FROM "MediaProgress" AS progress
INNER JOIN "Works" AS work ON work."Id"=progress."WorkId"
WHERE work."CanonicalTitle" LIKE 'Checkpoint page fixture %';
COMMIT;
ANALYZE "MediaProgress";
ANALYZE "TimeProgressPositions";
ANALYZE "Works";
SELECT progress."PublicId" AS expected_first
FROM "MediaProgress" AS progress WHERE progress."ProfileId"=:profile_id AND NOT progress."IsCompleted"
ORDER BY progress."LastActivityAt" DESC, progress."Id" DESC LIMIT 1 \gset
EXECUTE phase_b_continue(:actor_a,:profile_id,1,0) \gset
SELECT 1 / CASE WHEN :'MediaProgressId'::uuid=:'expected_first'::uuid AND :PositionMs=12345 THEN 1 ELSE 0 END;
EXECUTE phase_b_continue(:actor_a,:profile_id,25,0);
SELECT 1 / CASE WHEN :ROW_COUNT=25 THEN 1 ELSE 0 END;
EXECUTE phase_b_continue(:actor_a,:profile_id,101,0);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_continue(:actor_a,:profile_id,25,100001);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXPLAIN (ANALYZE, BUFFERS, TIMING FALSE) EXECUTE phase_b_continue(:actor_a,:profile_id,25,0);
DEALLOCATE phase_b_continue;
DEALLOCATE phase_b_progress_checkpoint;
DEALLOCATE phase_b_progress_receipt;
DEALLOCATE phase_b_progress_start;
DEALLOCATE phase_b_progress_target;
DEALLOCATE phase_b_progress_snapshot;
