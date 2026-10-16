#!/usr/bin/env bash
set -euo pipefail
if [[ "${PGDATABASE:-}" != "phase_b_scratch" || "${PGUSER:-}" != "phase_b_ci" ]]; then
    echo "Refusing Music concurrency test outside Phase-B scratch database" >&2
    exit 1
fi
scratch="$(mktemp -d /tmp/phase_b_music_race.XXXXXX)"
export PHASE_B_MUSIC_RACE_READY="$scratch/ready"
winner=""
retry=""
metadata=""
trap 'for pid in "$winner" "$retry" "$metadata"; do if [[ -n "$pid" ]]; then kill "$pid" 2>/dev/null || true; fi; done; rm -f "$scratch/ready" "$scratch/winner.log" "$scratch/retry.log" "$scratch/metadata.log"; rmdir "$scratch"' EXIT
psql -X -q -v ON_ERROR_STOP=1 > "$scratch/winner.log" 2>&1 <<'SQL' &
\i /tmp/phase_b_monitoring_begin_artist_prepared.sql
\i /tmp/phase_b_monitoring_reset_artist_prepared.sql
\i /tmp/phase_b_monitoring_source_on_prepared.sql
\i /tmp/phase_b_monitoring_source_off_prepared.sql
\i /tmp/phase_b_monitoring_future_artist_prepared.sql
\i /tmp/phase_b_monitoring_complete_artist_prepared.sql
BEGIN;
SET LOCAL statement_timeout='15s';
SET LOCAL lock_timeout='10s';
SELECT "Id" AS actor_id FROM "Accounts" WHERE "Email"='music-lifecycle-a@example.invalid' \gset
SELECT "Id" AS profile_id FROM "Profiles" WHERE "DisplayName"='Music lifecycle profile' \gset
SELECT "PublicId" AS artist_public FROM "People" WHERE "DisplayName"='Lifecycle artist' \gset
EXECUTE phase_b_monitoring_begin_artist(:actor_id,:profile_id,:'artist_public','8b523700-4071-42ef-b793-f8bc92111901',TRUE,TRUE,now()) \gset
SELECT :Id AS command_operation \gset
\! touch "$PHASE_B_MUSIC_RACE_READY"
DO $overlap$
DECLARE
    deadline timestamptz := clock_timestamp() + INTERVAL '8 seconds';
BEGIN
    LOOP
        PERFORM pg_stat_clear_snapshot();
        EXIT WHEN (SELECT count(*) FROM pg_stat_activity AS activity
            WHERE activity.application_name IN ('phase_b_music_retry', 'phase_b_music_metadata')
                AND cardinality(pg_blocking_pids(activity.pid)) > 0) = 2;
        IF clock_timestamp() >= deadline THEN
            RAISE EXCEPTION 'Retry and metadata did not overlap the held artist command';
        END IF;
        PERFORM pg_sleep(0.05);
    END LOOP;
END $overlap$;
EXECUTE phase_b_monitoring_reset_artist(:actor_id,:profile_id,:command_operation);
EXECUTE phase_b_monitoring_source_on(:actor_id,:profile_id,:command_operation,now());
EXECUTE phase_b_monitoring_source_off(:actor_id,:profile_id,:command_operation);
EXECUTE phase_b_monitoring_future_artist(:actor_id,:profile_id,:command_operation);
EXECUTE phase_b_monitoring_complete_artist(:actor_id,:profile_id,:command_operation,now());
SELECT 1 / CASE WHEN :ROW_COUNT=1 THEN 1 ELSE 0 END;
COMMIT;
SQL
winner=$!
for attempt in {1..100}; do
    if [[ -f "$PHASE_B_MUSIC_RACE_READY" ]]; then break; fi
    if ! kill -0 "$winner" 2>/dev/null; then break; fi
    sleep 0.05
done
if [[ ! -f "$PHASE_B_MUSIC_RACE_READY" ]]; then
    cat "$scratch/winner.log" >&2
    exit 1
fi
PGAPPNAME=phase_b_music_metadata psql -X -q -v ON_ERROR_STOP=1 > "$scratch/metadata.log" 2>&1 <<'SQL' &
BEGIN;
SET LOCAL statement_timeout='15s';
SET LOCAL lock_timeout='10s';
SELECT "Id" AS artist_id FROM "People" WHERE "DisplayName"='Lifecycle artist' \gset
SELECT "Id" FROM "MusicArtists" WHERE "Id"=:artist_id FOR UPDATE;
INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (6,'Music concurrent later album') RETURNING "Id" AS work_id \gset
INSERT INTO "MusicAlbums" ("WorkId", "MusicAlbumTypeId") VALUES (:work_id,1);
INSERT INTO "WorkMusicArtists" ("WorkId", "MusicArtistId") VALUES (:work_id,:artist_id);
COMMIT;
SQL
metadata=$!
PGAPPNAME=phase_b_music_retry psql -X -q -v ON_ERROR_STOP=1 > "$scratch/retry.log" 2>&1 <<'SQL' &
\i /tmp/phase_b_monitoring_begin_artist_prepared.sql
\i /tmp/phase_b_monitoring_receipt_prepared.sql
BEGIN;
SET LOCAL statement_timeout='15s';
SET LOCAL lock_timeout='10s';
SELECT "Id" AS actor_id FROM "Accounts" WHERE "Email"='music-lifecycle-a@example.invalid' \gset
SELECT "Id" AS profile_id FROM "Profiles" WHERE "DisplayName"='Music lifecycle profile' \gset
SELECT "PublicId" AS artist_public FROM "People" WHERE "DisplayName"='Lifecycle artist' \gset
EXECUTE phase_b_monitoring_begin_artist(:actor_id,:profile_id,:'artist_public','8b523700-4071-42ef-b793-f8bc92111901',TRUE,TRUE,now());
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_monitoring_receipt(:actor_id,:profile_id,:'artist_public','8b523700-4071-42ef-b793-f8bc92111901',TRUE,TRUE);
SELECT 1 / CASE WHEN :ROW_COUNT=1 THEN 1 ELSE 0 END;
COMMIT;
SQL
retry=$!
if ! wait "$winner"; then cat "$scratch/winner.log" >&2; exit 1; fi
winner=""
if ! wait "$retry"; then cat "$scratch/retry.log" >&2; exit 1; fi
retry=""
if ! wait "$metadata"; then cat "$scratch/metadata.log" >&2; exit 1; fi
metadata=""
psql -X -q -v ON_ERROR_STOP=1 <<'SQL'
\i /tmp/phase_b_monitoring_effective_prepared.sql
SELECT "Id" AS actor_id FROM "Accounts" WHERE "Email"='music-lifecycle-a@example.invalid' \gset
SELECT "Id" AS profile_id FROM "Profiles" WHERE "DisplayName"='Music lifecycle profile' \gset
SELECT "PublicId" AS later_public FROM "Works" WHERE "CanonicalTitle"='Music concurrent later album' \gset
SELECT "PublicId" AS known_public FROM "Works" WHERE "CanonicalTitle"='Music lifecycle album A' \gset
SELECT 1 / CASE WHEN (SELECT count(*) FROM "Operations"
    WHERE "IdempotencyKey"='monitoring_artist:8b523700-4071-42ef-b793-f8bc92111901' AND "OperationStatusTypeId"=3) = 1 THEN 1 ELSE 0 END;
SELECT 1 / CASE WHEN NOT EXISTS (SELECT 1 FROM "WorkMonitoringDecisions" AS decision
    INNER JOIN "Works" AS work ON work."Id"=decision."WorkId"
    WHERE work."PublicId"=:'later_public'::uuid) THEN 1 ELSE 0 END;
EXECUTE phase_b_monitoring_effective(:actor_id,:profile_id,ARRAY[:'later_public'::uuid]) \gset
SELECT 1 / CASE WHEN :'IsWorkMonitored'::boolean=TRUE THEN 1 ELSE 0 END;
EXECUTE phase_b_monitoring_effective(:actor_id,:profile_id,ARRAY[:'known_public'::uuid]) \gset
SELECT 1 / CASE WHEN :'IsWorkMonitored'::boolean=FALSE THEN 1 ELSE 0 END;
SELECT 1 / CASE WHEN EXISTS (SELECT 1 FROM "WorkMonitoringDecisions" AS decision
    INNER JOIN "WorkTracks" AS track ON track."Id"=decision."WorkTrackId"
    INNER JOIN "Works" AS work ON work."Id"=track."WorkId"
    WHERE work."PublicId"=:'known_public'::uuid AND decision."IsMonitored"=TRUE) THEN 1 ELSE 0 END;
SQL
echo "Artist Future has one command winner, replay receipt and a later inherited album"
