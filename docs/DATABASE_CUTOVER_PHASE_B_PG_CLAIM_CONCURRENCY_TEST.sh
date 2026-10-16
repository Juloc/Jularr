#!/usr/bin/env bash
# Phase B B12 scratch-only: exercise the ACTUAL canonical Operations SKIP LOCKED
# claim query through two independent psql sessions. No dev/production database.
set -euo pipefail
if [[ "${PGDATABASE:-}" != "phase_b_scratch" || "${PGUSER:-}" != "phase_b_ci" ]]; then
  echo "Refusing claim concurrency test outside Phase-B scratch database" >&2
  exit 1
fi
ready=/tmp/phase_b_claim_lock_ready
rm -f "$ready"

psql -X -v ON_ERROR_STOP=1 <<'SQL'
INSERT INTO "Operations"
 ("OperationKindKey","OperationStatusTypeId","IdempotencyKey","NextAttemptAt")
VALUES
 ('ci-claim',1,'phase-b-claim-locked',NULL),
 ('ci-claim',1,'phase-b-claim-free-1',now()-interval '1 minute'),
 ('ci-claim',1,'phase-b-claim-free-2',now()-interval '1 minute'),
 ('ci-claim',1,'phase-b-claim-future',now()+interval '1 day');
SQL

# Hold a row lock in a separate connection, signal only AFTER the lock exists.
# If the implementation does not SKIP LOCKED, the tested claim times out.
psql -X -v ON_ERROR_STOP=1 > /tmp/phase_b_claim_lock.log 2>&1 <<'SQL' &
BEGIN;
SELECT "Id" FROM "Operations"
WHERE "IdempotencyKey"='phase-b-claim-locked' FOR UPDATE;
\! touch /tmp/phase_b_claim_lock_ready
SELECT pg_sleep(7);
ROLLBACK;
SQL
lock_pid=$!
trap 'kill "$lock_pid" 2>/dev/null || true' EXIT
for i in {1..80}; do
  if [[ -f "$ready" ]]; then break; fi
  if ! kill -0 "$lock_pid" 2>/dev/null; then break; fi
  sleep 0.1
done
if [[ ! -f "$ready" ]]; then
  cat /tmp/phase_b_claim_lock.log >&2
  echo "Timed out waiting for independent PostgreSQL row lock" >&2
  exit 1
fi

psql -X -v ON_ERROR_STOP=1 <<'SQL'
\i /tmp/phase_b_claim_prepared.sql
SET statement_timeout='2s';
BEGIN;
EXECUTE phase_b_claim(1,2,3,'ci-worker-A');
COMMIT;
DO $verify$
BEGIN
  IF (SELECT count(*) FROM "Operations" WHERE "IdempotencyKey" LIKE 'phase-b-claim-%'
          AND "OperationStatusTypeId"=2 AND "ClaimedBy"='ci-worker-A'
          AND "AttemptCount"=1) <> 2 THEN
    RAISE EXCEPTION 'Claim did not take exactly the 2 unlocked due operations';
  END IF;
  IF (SELECT count(*) FROM "Operations" WHERE "IdempotencyKey" IN
          ('phase-b-claim-locked','phase-b-claim-future')
          AND "OperationStatusTypeId"=1 AND "AttemptCount"=0) <> 2 THEN
    RAISE EXCEPTION 'Claim touched locked or future job';
  END IF;
END $verify$;
DEALLOCATE phase_b_claim;
SQL

wait "$lock_pid"
trap - EXIT

# The previously locked row is now due; future row must remain untouched.
psql -X -v ON_ERROR_STOP=1 <<'SQL'
\i /tmp/phase_b_claim_prepared.sql
BEGIN;
EXECUTE phase_b_claim(1,2,3,'ci-worker-B');
COMMIT;
-- A third claim is idempotently empty because due jobs left pending=0.
BEGIN;
EXECUTE phase_b_claim(1,2,3,'ci-worker-C');
COMMIT;
DO $verify$
BEGIN
  IF (SELECT count(*) FROM "Operations" WHERE "IdempotencyKey" LIKE 'phase-b-claim-%'
         AND "OperationStatusTypeId"=2 AND "AttemptCount"=1) <> 3 THEN
     RAISE EXCEPTION 'Failed to claim each due job exactly once';
  END IF;
  IF (SELECT count(*) FROM "Operations" WHERE "IdempotencyKey"='phase-b-claim-locked'
         AND "ClaimedBy"='ci-worker-B') <> 1 THEN
     RAISE EXCEPTION 'Previously locked job not claimed by later worker';
  END IF;
  IF (SELECT count(*) FROM "Operations" WHERE "IdempotencyKey"='phase-b-claim-future'
         AND "OperationStatusTypeId"=1 AND "AttemptCount"=0) <> 1 THEN
     RAISE EXCEPTION 'Future due-time job was prematurely claimed';
  END IF;
END $verify$;
DEALLOCATE phase_b_claim;
SQL
echo "Phase B concurrent SKIP LOCKED claim and retry exclusion passed"
