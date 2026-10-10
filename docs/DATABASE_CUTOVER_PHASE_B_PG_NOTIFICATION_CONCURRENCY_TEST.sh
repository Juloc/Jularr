#!/usr/bin/env bash
set -euo pipefail
if [[ "${PGDATABASE:-}" != "phase_b_scratch" || "${PGUSER:-}" != "phase_b_ci" ]]; then
    echo "Refusing notification concurrency test outside Phase-B scratch database" >&2
    exit 1
fi
ready=/tmp/phase_b_notification_lock_ready
rm -f "$ready"

psql -X -q -v ON_ERROR_STOP=1 <<'SQL'
BEGIN;
INSERT INTO "UiLocales" ("Locale", "Name") VALUES ('phase-b-notification-race', 'Notification race') RETURNING "Id" AS locale_id \gset
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('phase-b-notification-race@example.invalid', 'Notification race', 1) RETURNING "Id" AS account_id \gset
INSERT INTO "Profiles" ("OwnerAccountId", "DisplayName", "UiLocaleId")
VALUES (:account_id, 'Notification race', :locale_id) RETURNING "Id" AS profile_id \gset
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (:account_id, :profile_id);
INSERT INTO "Events" (
    "NotificationEventCategoryTypeId", "EventAudienceTypeId", "EventSeverityTypeId", "ProfileId", "MessageKey", "DedupKey")
VALUES (1, 1, 1, :profile_id, 'notification_race_a', 'phase_b_notification_race'),
       (1, 1, 1, :profile_id, 'notification_race_b', 'phase_b_notification_race');
COMMIT;
SQL

psql -X -q -v ON_ERROR_STOP=1 > /tmp/phase_b_notification_lock.log 2>&1 <<'SQL' &
\i /tmp/phase_b_inbox_ensure_prepared.sql
\i /tmp/phase_b_inbox_record_prepared.sql
BEGIN;
SET LOCAL statement_timeout = '10s';
SELECT "Id" AS event_id, "ProfileId" AS profile_id FROM "Events" WHERE "MessageKey" = 'notification_race_a' \gset
EXECUTE phase_b_inbox_ensure(:'event_id', :profile_id) \gset
\set inbox_id :Id
\! touch /tmp/phase_b_notification_lock_ready
SELECT pg_sleep(2);
EXECUTE phase_b_inbox_record(:inbox_id, :profile_id, :'event_id');
COMMIT;
SQL
lock_pid=$!
trap 'kill "$lock_pid" 2>/dev/null || true' EXIT
for attempt in {1..80}; do
    if [[ -f "$ready" ]]; then break; fi
    if ! kill -0 "$lock_pid" 2>/dev/null; then break; fi
    sleep 0.1
done
if [[ ! -f "$ready" ]]; then
    cat /tmp/phase_b_notification_lock.log >&2
    echo "Timed out waiting for notification transaction" >&2
    exit 1
fi

psql -X -q -v ON_ERROR_STOP=1 <<'SQL'
\i /tmp/phase_b_inbox_ensure_prepared.sql
\i /tmp/phase_b_inbox_record_prepared.sql
BEGIN;
SET LOCAL statement_timeout = '10s';
SELECT "Id" AS event_id, "ProfileId" AS profile_id FROM "Events" WHERE "MessageKey" = 'notification_race_b' \gset
EXECUTE phase_b_inbox_ensure(:'event_id', :profile_id) \gset
\set inbox_id :Id
EXECUTE phase_b_inbox_record(:inbox_id, :profile_id, :'event_id');
COMMIT;
BEGIN;
EXECUTE phase_b_inbox_ensure(:'event_id', :profile_id);
EXECUTE phase_b_inbox_record(:inbox_id, :profile_id, :'event_id');
COMMIT;
DO $race_result$
BEGIN
    IF (SELECT COUNT(*) FROM "Notifications" AS inbox WHERE inbox."NotificationGroupKey" = 'dedup:phase_b_notification_race') <> 1
        OR (SELECT inbox."OccurrenceCount" FROM "Notifications" AS inbox WHERE inbox."NotificationGroupKey" = 'dedup:phase_b_notification_race') <> 2
        OR (SELECT COUNT(*) FROM "NotificationEvents" AS occurrence WHERE occurrence."NotificationGroupKey" = 'dedup:phase_b_notification_race') <> 2 THEN
        RAISE EXCEPTION 'Concurrent notification recurrence was duplicated or lost';
    END IF;
END $race_result$;
SQL
wait "$lock_pid"
trap - EXIT

psql -X -q -v ON_ERROR_STOP=1 <<'SQL'
BEGIN;
DELETE FROM "NotificationEvents" WHERE "NotificationGroupKey" = 'dedup:phase_b_notification_race';
DELETE FROM "Notifications" WHERE "NotificationGroupKey" = 'dedup:phase_b_notification_race';
DELETE FROM "Events" WHERE "DedupKey" = 'phase_b_notification_race';
DELETE FROM "AccountProfiles" WHERE "ProfileId" IN (SELECT "Id" FROM "Profiles" WHERE "DisplayName" = 'Notification race');
DELETE FROM "Profiles" WHERE "DisplayName" = 'Notification race';
DELETE FROM "Accounts" WHERE "Email" = 'phase-b-notification-race@example.invalid';
DELETE FROM "UiLocales" WHERE "Locale" = 'phase-b-notification-race';
COMMIT;
SQL
rm -f "$ready" /tmp/phase_b_notification_lock.log
echo "Notification concurrent recurrence, replay and commit-time projection passed"
