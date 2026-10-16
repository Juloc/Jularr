#!/usr/bin/env bash
set -euo pipefail
if [[ "${PGDATABASE:-}" != "phase_b_scratch" || "${PGUSER:-}" != "phase_b_ci" ]]; then
    echo "Refusing Reader concurrency test outside Phase-B scratch database" >&2
    exit 1
fi
scratch="$(mktemp -d /tmp/phase_b_reader_race.XXXXXX)"
export PHASE_B_READER_RACE_READY="$scratch/ready"
winner=""
late_writer=""
trap 'if [[ -n "$winner" ]]; then kill "$winner" 2>/dev/null || true; fi; if [[ -n "$late_writer" ]]; then kill "$late_writer" 2>/dev/null || true; fi; rm -f "$scratch/ready" "$scratch/publish.log" "$scratch/late.log"; rmdir "$scratch"' EXIT
psql -X -q -v ON_ERROR_STOP=1 > "$scratch/publish.log" 2>&1 <<'SQL' &
\i /tmp/phase_b_reader_publish_prepared.sql
BEGIN;
SET LOCAL statement_timeout='10s';
SET LOCAL lock_timeout='8s';
SELECT content."Id" AS content_id
FROM "ReaderContent" AS content INNER JOIN "WorkEditions" AS edition ON edition."Id"=content."WorkEditionId"
WHERE edition."Name"='Reader content fixture' AND content."VariantKey"='race' \gset
EXECUTE phase_b_reader_publish(:content_id,decode(repeat('a7',32),'hex'),now(),'reader-content-builder');
SELECT 1 / CASE WHEN :ROW_COUNT=1 THEN 1 ELSE 0 END;
\! touch "$PHASE_B_READER_RACE_READY"
SELECT pg_sleep(2);
COMMIT;
SQL
winner=$!
for attempt in {1..80}; do
    if [[ -f "$PHASE_B_READER_RACE_READY" ]]; then break; fi
    if ! kill -0 "$winner" 2>/dev/null; then break; fi
    sleep 0.05
done
if [[ ! -f "$PHASE_B_READER_RACE_READY" ]]; then
    cat "$scratch/publish.log" >&2
    exit 1
fi
psql -X -q -v ON_ERROR_STOP=1 > "$scratch/late.log" 2>&1 <<'SQL' &
SET statement_timeout='10s';
SET lock_timeout='8s';
DO $late$
DECLARE
    content_id bigint;
    rejected text;
BEGIN
    SELECT content."Id" INTO content_id
    FROM "ReaderContent" AS content INNER JOIN "WorkEditions" AS edition ON edition."Id"=content."WorkEditionId"
    WHERE edition."Name"='Reader content fixture' AND content."VariantKey"='race';
    BEGIN
        INSERT INTO "ReaderContentNodes" ("ReaderContentId", "StableId", "OrderIndex", "ReaderNodeTypeId")
        VALUES (content_id,'concurrent-late-race',1,3);
        RAISE EXCEPTION 'Concurrent builder mutated a published revision';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'CK_ReaderContent_ImmutableIndex' THEN RAISE; END IF;
    END;
END $late$;
SQL
late_writer=$!
psql -X -q -v ON_ERROR_STOP=1 <<'SQL'
\i /tmp/phase_b_reader_publish_prepared.sql
BEGIN;
SET LOCAL statement_timeout='10s';
SET LOCAL lock_timeout='8s';
SELECT content."Id" AS content_id
FROM "ReaderContent" AS content INNER JOIN "WorkEditions" AS edition ON edition."Id"=content."WorkEditionId"
WHERE edition."Name"='Reader content fixture' AND content."VariantKey"='race' \gset
EXECUTE phase_b_reader_publish(:content_id,decode(repeat('a7',32),'hex'),now(),'reader-content-builder');
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
COMMIT;
SQL
wait "$winner"
winner=""
if ! wait "$late_writer"; then
    cat "$scratch/late.log" >&2
    exit 1
fi
late_writer=""
psql -X -q -v ON_ERROR_STOP=1 <<'SQL'
DO $race$
DECLARE
    content_id bigint;
    rejected text;
BEGIN
    SELECT content."Id" INTO content_id
    FROM "ReaderContent" AS content INNER JOIN "WorkEditions" AS edition ON edition."Id"=content."WorkEditionId"
    WHERE edition."Name"='Reader content fixture' AND content."VariantKey"='race';
    IF NOT (SELECT "PublishedAt" IS NOT NULL FROM "ReaderContent" WHERE "Id"=content_id)
        OR (SELECT count(*) FROM "ReaderContentNodes" WHERE "ReaderContentId"=content_id) <> 1 THEN
        RAISE EXCEPTION 'Concurrent publication left partial state';
    END IF;
    BEGIN
        INSERT INTO "ReaderContentNodes" ("ReaderContentId", "StableId", "OrderIndex", "ReaderNodeTypeId")
        VALUES (content_id,'late-race',1,3);
        RAISE EXCEPTION 'Late builder mutated a published revision';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'CK_ReaderContent_ImmutableIndex' THEN RAISE; END IF;
    END;
END $race$;
SQL
echo "Reader publication has one winner and an immutable complete index"
