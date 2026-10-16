#!/usr/bin/env bash
set -euo pipefail
if [[ "${PGDATABASE:-}" != "phase_b_scratch" || "${PGUSER:-}" != "phase_b_ci" ]]; then
    echo "Refusing progress concurrency test outside Phase-B scratch database" >&2
    exit 1
fi
ready=/tmp/phase_b_progress_lock_ready
rm -f "$ready"
psql -X -q -v ON_ERROR_STOP=1 <<'SQL'
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId")
SELECT account."Id", profile."Id"
FROM "Accounts" AS account CROSS JOIN "Profiles" AS profile
WHERE account."Email"='checkpoint-b@example.invalid' AND profile."DisplayName"='Checkpoint A';
INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (1,'Concurrent progress start');
SQL
psql -X -q -v ON_ERROR_STOP=1 > /tmp/phase_b_progress_winner.log 2>&1 <<'SQL' &
\i /tmp/phase_b_progress_checkpoint_prepared.sql
\i /tmp/phase_b_progress_start_prepared.sql
BEGIN;
SET LOCAL statement_timeout='10s';
SET LOCAL lock_timeout='8s';
SELECT progress."PublicId" AS progress_public, progress."ProfileId" AS profile_id,
    profile."OwnerAccountId" AS actor_id
FROM "MediaProgress" AS progress
INNER JOIN "Works" AS work ON work."Id"=progress."WorkId"
INNER JOIN "Profiles" AS profile ON profile."Id"=progress."ProfileId"
WHERE work."CanonicalTitle"='Checkpoint time' \gset
SELECT "PublicId" AS start_work_public FROM "Works" WHERE "CanonicalTitle"='Concurrent progress start' \gset
EXECUTE phase_b_progress_start(:actor_id,:profile_id,:'start_work_public',NULL,NULL,NULL,NULL,1,now());
SELECT 1 / CASE WHEN :ROW_COUNT=1 THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_checkpoint(:actor_id,:profile_id,:'progress_public',5,
    '00000000-0000-4000-8000-000000000051',decode(repeat('51',32),'hex'),1,now(),NULL,1,false,
    4000,10000,NULL,NULL,NULL,NULL,NULL,NULL,NULL);
SELECT 1 / CASE WHEN :ROW_COUNT=1 THEN 1 ELSE 0 END;
\! touch /tmp/phase_b_progress_lock_ready
SELECT pg_sleep(2);
COMMIT;
SQL
winner=$!
trap 'kill "$winner" 2>/dev/null || true' EXIT
for attempt in {1..80}; do
    if [[ -f "$ready" ]]; then break; fi
    if ! kill -0 "$winner" 2>/dev/null; then break; fi
    sleep 0.05
done
if [[ ! -f "$ready" ]]; then
    cat /tmp/phase_b_progress_winner.log >&2
    exit 1
fi
psql -X -q -v ON_ERROR_STOP=1 <<'SQL'
\i /tmp/phase_b_progress_checkpoint_prepared.sql
\i /tmp/phase_b_progress_start_prepared.sql
\i /tmp/phase_b_progress_target_prepared.sql
BEGIN;
SET LOCAL statement_timeout='10s';
SET LOCAL lock_timeout='8s';
SELECT progress."PublicId" AS progress_public, progress."ProfileId" AS profile_id
FROM "MediaProgress" AS progress INNER JOIN "Works" AS work ON work."Id"=progress."WorkId"
WHERE work."CanonicalTitle"='Checkpoint time' \gset
SELECT account."Id" AS actor_id FROM "Accounts" AS account WHERE account."Email"='checkpoint-b@example.invalid' \gset
SELECT "PublicId" AS start_work_public FROM "Works" WHERE "CanonicalTitle"='Concurrent progress start' \gset
EXECUTE phase_b_progress_start(:actor_id,:profile_id,:'start_work_public',NULL,NULL,NULL,NULL,1,now());
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_target(:actor_id,:profile_id,:'start_work_public',NULL,NULL,NULL,NULL,1);
SELECT 1 / CASE WHEN :ROW_COUNT=1 THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_checkpoint(:actor_id,:profile_id,:'progress_public',5,
    '00000000-0000-4000-8000-000000000052',decode(repeat('52',32),'hex'),2,now(),NULL,NULL,false,
    9000,10000,NULL,NULL,NULL,NULL,NULL,NULL,NULL);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
COMMIT;
SQL
wait "$winner"
trap - EXIT
psql -X -q -v ON_ERROR_STOP=1 <<'SQL'
SELECT progress."Id" AS progress_id, progress."ProfileId" AS profile_id
FROM "MediaProgress" AS progress INNER JOIN "Works" AS work ON work."Id"=progress."WorkId"
WHERE work."CanonicalTitle"='Checkpoint time' \gset
SELECT 1 / CASE WHEN (SELECT "Revision"=6 AND "IsCompleted" AND "CompletionReasonTypeId"=1
    FROM "MediaProgress" WHERE "Id"=:progress_id)
    AND (SELECT "PositionMs"=4000 FROM "TimeProgressPositions" WHERE "MediaProgressId"=:progress_id)
    AND (SELECT count(*)=5 FROM "MediaProgressCheckpointEvents" WHERE "MediaProgressId"=:progress_id)
    AND NOT EXISTS (SELECT 1 FROM "MediaProgressCheckpointEvents"
        WHERE "ClientEventId"='00000000-0000-4000-8000-000000000052') THEN 1 ELSE 0 END;
SELECT 1 / CASE WHEN (SELECT count(*)=1 FROM "MediaProgress" AS progress
    INNER JOIN "Works" AS work ON work."Id"=progress."WorkId"
    INNER JOIN "TimeProgressPositions" AS position ON position."MediaProgressId"=progress."Id"
    WHERE work."CanonicalTitle"='Concurrent progress start' AND progress."ProfileId"=:profile_id) THEN 1 ELSE 0 END;
DELETE FROM "AccountProfiles" AS membership USING "Accounts" AS account
WHERE membership."AccountId"=account."Id" AND account."Email"='checkpoint-b@example.invalid'
    AND membership."ProfileId"=:profile_id;
SQL
rm -f "$ready"
echo "Different authorized accounts: one start and one checkpoint winner; no stale position, completion or receipt overwrite"
