#!/usr/bin/env bash
set -euo pipefail
if [[ "${PGDATABASE:-}" != "phase_b_scratch" || "${PGUSER:-}" != "phase_b_ci" ]]; then
    echo "Refusing authentication concurrency test outside Phase-B scratch database" >&2
    exit 1
fi
ready=/tmp/phase_b_auth_lock_ready
rm -f "$ready"
psql -X -q -v ON_ERROR_STOP=1 <<'SQL'
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('auth-race@example.invalid', 'Auth race', 2) RETURNING "Id" AS account_id \gset
INSERT INTO "AccountSessions" ("AccountId", "TokenHash", "ExpiresAt")
VALUES (:account_id, decode(repeat('71', 32), 'hex'), now() + INTERVAL '1 day');
INSERT INTO "AccountRecoveryCodes" ("AccountId", "CodeHash")
VALUES (:account_id, decode(repeat('81', 32), 'hex'));
SQL
psql -X -q -v ON_ERROR_STOP=1 > /tmp/phase_b_auth_winner.log 2>&1 <<'SQL' &
\i /tmp/phase_b_auth_rotate_prepared.sql
\i /tmp/phase_b_auth_recovery_prepared.sql
BEGIN;
SET LOCAL statement_timeout = '10s';
SELECT "Id" AS account_id FROM "Accounts" WHERE "Email" = 'auth-race@example.invalid' \gset
EXECUTE phase_b_auth_rotate(:account_id, decode(repeat('71', 32), 'hex'), decode(repeat('72', 32), 'hex'), 1, now(), now() + INTERVAL '1 day');
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
EXECUTE phase_b_auth_recovery(:account_id, decode(repeat('81', 32), 'hex'), now());
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
\! touch /tmp/phase_b_auth_lock_ready
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
    cat /tmp/phase_b_auth_winner.log >&2
    exit 1
fi
psql -X -q -v ON_ERROR_STOP=1 <<'SQL'
\i /tmp/phase_b_auth_rotate_prepared.sql
\i /tmp/phase_b_auth_recovery_prepared.sql
BEGIN;
SET LOCAL statement_timeout = '10s';
SELECT "Id" AS account_id FROM "Accounts" WHERE "Email" = 'auth-race@example.invalid' \gset
EXECUTE phase_b_auth_rotate(:account_id, decode(repeat('71', 32), 'hex'), decode(repeat('73', 32), 'hex'), 1, now(), now() + INTERVAL '1 day');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_auth_recovery(:account_id, decode(repeat('81', 32), 'hex'), now());
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
COMMIT;
SQL
wait "$winner"
trap - EXIT
psql -X -q -v ON_ERROR_STOP=1 <<'SQL'
BEGIN;
SELECT "Id" AS account_id FROM "Accounts" WHERE "Email" = 'auth-race@example.invalid' \gset
SELECT 1 / CASE WHEN (SELECT count(*) FROM "AccountSessions" WHERE "AccountId" = :account_id AND "RotationRevision" = 2 AND "TokenHash" = decode(repeat('72', 32), 'hex')) = 1 THEN 1 ELSE 0 END;
SELECT 1 / CASE WHEN (SELECT count(*) FROM "AccountRecoveryCodes" WHERE "AccountId" = :account_id AND "UsedAt" IS NOT NULL) = 1 THEN 1 ELSE 0 END;
DELETE FROM "AccountRecoveryCodes" WHERE "AccountId" = :account_id;
DELETE FROM "AccountSessions" WHERE "AccountId" = :account_id;
DELETE FROM "Accounts" WHERE "Id" = :account_id;
COMMIT;
SQL
rm -f "$ready"
echo "Concurrent session rotation and recovery consumption: one winner, replay unchanged"
