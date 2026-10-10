BEGIN;
SET LOCAL statement_timeout = '20s';
\o /dev/null
\i /tmp/phase_b_inbox_prepared.sql
\i /tmp/phase_b_inbox_ensure_prepared.sql
\i /tmp/phase_b_inbox_record_prepared.sql

INSERT INTO "UiLocales" ("Locale", "Name") VALUES ('phase-b-inbox', 'Inbox') RETURNING "Id" AS locale_id \gset
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('phase-b-inbox-owner@example.invalid', 'Inbox owner', 1) RETURNING "Id" AS owner_id \gset
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('phase-b-inbox-user@example.invalid', 'Inbox user', 2) RETURNING "Id" AS user_id \gset
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('phase-b-inbox-other@example.invalid', 'Other owner', 1) RETURNING "Id" AS other_id \gset
INSERT INTO "Profiles" ("OwnerAccountId", "DisplayName", "UiLocaleId")
VALUES (:owner_id, 'Inbox A', :locale_id) RETURNING "Id" AS profile_a \gset
INSERT INTO "Profiles" ("OwnerAccountId", "DisplayName", "UiLocaleId")
VALUES (:other_id, 'Inbox B', :locale_id) RETURNING "Id" AS profile_b \gset
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (:owner_id, :profile_a), (:other_id, :profile_b);

INSERT INTO "Events" (
    "NotificationEventCategoryTypeId", "EventAudienceTypeId", "EventSeverityTypeId", "ProfileId", "MessageKey", "CreatedAt")
SELECT 1, 1, 1, :profile_a, 'inbox_load', '2026-10-01 12:00Z'::timestamptz
FROM generate_series(1, 1000);
INSERT INTO "Events" (
    "NotificationEventCategoryTypeId", "EventAudienceTypeId", "EventSeverityTypeId", "ProfileId", "MessageKey", "CreatedAt")
VALUES (1, 1, 1, :profile_b, 'private_inbox', '2026-10-01 12:00Z');
INSERT INTO "Notifications" ("EventId", "ProfileId", "NotificationGroupKey", "CreatedAt", "LastOccurredAt")
SELECT event."Id", event."ProfileId", event."NotificationGroupKey", event."CreatedAt", event."CreatedAt"
FROM "Events" AS event WHERE event."ProfileId" IN (:profile_a, :profile_b);
INSERT INTO "NotificationEvents" ("NotificationId", "EventId", "ProfileId", "NotificationGroupKey")
SELECT inbox."Id", inbox."EventId", inbox."ProfileId", inbox."NotificationGroupKey"
FROM "Notifications" AS inbox WHERE inbox."ProfileId" IN (:profile_a, :profile_b);
SELECT "PublicId" AS first_id, "Id" AS first_internal_id FROM "Notifications"
WHERE "ProfileId" = :profile_a ORDER BY "LastOccurredAt" DESC, "Id" DESC LIMIT 1 \gset
SELECT "PublicId" AS next_id FROM "Notifications"
WHERE "ProfileId" = :profile_a ORDER BY "LastOccurredAt" DESC, "Id" DESC LIMIT 1 OFFSET 25 \gset

EXECUTE phase_b_inbox(:owner_id, :profile_a, false, 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 25 THEN 1 ELSE 0 END;
EXECUTE phase_b_inbox(:owner_id, :profile_a, false, 1, 25) \gset
SELECT 1 / CASE WHEN :'NotificationId'::uuid = :'next_id'::uuid THEN 1 ELSE 0 END;
EXECUTE phase_b_inbox(:owner_id, :profile_a, false, 100, 995);
SELECT 1 / CASE WHEN :ROW_COUNT = 5 THEN 1 ELSE 0 END;
EXECUTE phase_b_inbox(:owner_id, :profile_b, false, 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_inbox(:user_id, :profile_a, false, 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_inbox(:owner_id, :profile_a, false, 101, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_inbox(:owner_id, :profile_a, false, 25, 100001);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;

UPDATE "Notifications" SET "ReadAt" = '2026-10-02 12:00Z' WHERE "Id" = :first_internal_id;
EXECUTE phase_b_inbox(:owner_id, :profile_a, false, 1, 0) \gset
SELECT 1 / CASE WHEN :'NotificationId'::uuid = :'first_id'::uuid THEN 1 ELSE 0 END;
EXECUTE phase_b_inbox(:owner_id, :profile_a, true, 100, 900);
SELECT 1 / CASE WHEN :ROW_COUNT = 99 THEN 1 ELSE 0 END;
UPDATE "Notifications" SET "DismissedAt" = '2026-10-02 12:00Z' WHERE "Id" = :first_internal_id;
EXECUTE phase_b_inbox(:owner_id, :profile_a, false, 100, 995);
SELECT 1 / CASE WHEN :ROW_COUNT = 4 THEN 1 ELSE 0 END;
UPDATE "Notifications" SET "DismissedAt" = NULL WHERE "Id" = :first_internal_id;

INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (:user_id, :profile_a);
EXECUTE phase_b_inbox(:user_id, :profile_a, false, 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 25 THEN 1 ELSE 0 END;
DELETE FROM "AccountProfiles" WHERE "AccountId" = :user_id AND "ProfileId" = :profile_a;
EXECUTE phase_b_inbox(:user_id, :profile_a, false, 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
UPDATE "Accounts" SET "IsEnabled" = false WHERE "Id" = :owner_id;
EXECUTE phase_b_inbox(:owner_id, :profile_a, false, 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
UPDATE "Accounts" SET "IsEnabled" = true WHERE "Id" = :owner_id;

INSERT INTO "Events" ("NotificationEventCategoryTypeId", "EventAudienceTypeId", "EventSeverityTypeId", "MessageKey")
VALUES (8, 2, 3, 'admin_inbox') RETURNING "Id" AS admin_event \gset
INSERT INTO "Notifications" ("EventId", "AccountId", "NotificationGroupKey")
VALUES (:'admin_event', :owner_id, 'event:' || :'admin_event'), (:'admin_event', :user_id, 'event:' || :'admin_event');
INSERT INTO "NotificationEvents" ("NotificationId", "EventId", "ProfileId", "NotificationGroupKey")
SELECT inbox."Id", inbox."EventId", inbox."ProfileId", inbox."NotificationGroupKey"
FROM "Notifications" AS inbox WHERE inbox."EventId" = :'admin_event';
EXECUTE phase_b_inbox(:owner_id, NULL, false, 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
EXECUTE phase_b_inbox(:user_id, NULL, false, 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_inbox(:other_id, NULL, false, 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
UPDATE "Accounts" SET "AccountRoleTypeId" = 2 WHERE "Id" = :owner_id;
EXECUTE phase_b_inbox(:owner_id, NULL, false, 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
UPDATE "Accounts" SET "AccountRoleTypeId" = 1 WHERE "Id" = :owner_id;

INSERT INTO "Events" (
    "NotificationEventCategoryTypeId", "EventAudienceTypeId", "EventSeverityTypeId", "ProfileId", "MessageKey", "DedupKey", "CreatedAt")
VALUES (1, 1, 1, :profile_a, 'recurrence', 'inbox_recurrence', '2026-10-03 12:00Z') RETURNING "Id" AS occurrence_a \gset
EXECUTE phase_b_inbox_ensure(:'occurrence_a', :profile_a) \gset
\set recurrence_id :Id
EXECUTE phase_b_inbox_record(:recurrence_id, :profile_a, :'occurrence_a') \gset
SELECT 1 / CASE WHEN :OccurrenceCount = 1 THEN 1 ELSE 0 END;
UPDATE "Notifications" SET "ReadAt" = '2026-10-04 12:00Z', "DismissedAt" = '2026-10-04 12:00Z' WHERE "Id" = :recurrence_id;
EXECUTE phase_b_inbox_ensure(:'occurrence_a', :profile_a);
EXECUTE phase_b_inbox_record(:recurrence_id, :profile_a, :'occurrence_a');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
SELECT 1 / CASE WHEN "OccurrenceCount" = 1 AND "ReadAt" IS NOT NULL AND "DismissedAt" IS NOT NULL THEN 1 ELSE 0 END
FROM "Notifications" WHERE "Id" = :recurrence_id;
INSERT INTO "Events" (
    "NotificationEventCategoryTypeId", "EventAudienceTypeId", "EventSeverityTypeId", "ProfileId", "MessageKey", "DedupKey", "CreatedAt")
VALUES (1, 1, 1, :profile_a, 'recurrence', 'inbox_recurrence', '2026-10-05 12:00Z') RETURNING "Id" AS occurrence_b \gset
EXECUTE phase_b_inbox_ensure(:'occurrence_b', :profile_a) \gset
SELECT 1 / CASE WHEN :Id = :recurrence_id THEN 1 ELSE 0 END;
EXECUTE phase_b_inbox_record(:recurrence_id, :profile_a, :'occurrence_b') \gset
SELECT 1 / CASE WHEN :OccurrenceCount = 2 THEN 1 ELSE 0 END;
SELECT 1 / CASE WHEN "CreatedAt" = '2026-10-03 12:00Z' AND "LastOccurredAt" = '2026-10-05 12:00Z'
    AND "ReadAt" IS NULL AND "DismissedAt" IS NULL AND "EventId" = :'occurrence_b'::uuid THEN 1 ELSE 0 END
FROM "Notifications" WHERE "Id" = :recurrence_id;
UPDATE "Notifications" SET "ReadAt" = '2026-10-06 12:00Z' WHERE "Id" = :recurrence_id;
EXECUTE phase_b_inbox_ensure(:'occurrence_a', :profile_a);
EXECUTE phase_b_inbox_record(:recurrence_id, :profile_a, :'occurrence_a');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
SELECT 1 / CASE WHEN "OccurrenceCount" = 2 AND "ReadAt" IS NOT NULL THEN 1 ELSE 0 END
FROM "Notifications" WHERE "Id" = :recurrence_id;
EXECUTE phase_b_inbox_ensure(:'occurrence_a', :profile_b);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_inbox_record(:recurrence_id, :profile_b, :'occurrence_b');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;

SET CONSTRAINTS ALL IMMEDIATE;
DO $inbox_integrity$
DECLARE
    rejected_constraint text;
BEGIN
    IF EXISTS (
        SELECT 1 FROM "Events" AS event
        WHERE event."MessageKey" = 'inbox_load' AND event."NotificationGroupKey" <> 'event:' || event."Id"::text
    ) THEN
        RAISE EXCEPTION 'Ungrouped event identity collided with supplied dedup keys';
    END IF;
    BEGIN
        UPDATE "Notifications" SET "OccurrenceCount" = "OccurrenceCount" + 1 WHERE "NotificationGroupKey" = 'dedup:inbox_recurrence';
        RAISE EXCEPTION 'Inbox committed an unrecorded recurrence';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'CK_Notifications_RecordedOccurrences' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "Notifications" SET "NotificationGroupKey" = 'wrong_group' WHERE "NotificationGroupKey" = 'dedup:inbox_recurrence';
        RAISE EXCEPTION 'Inbox accepted an unrelated event group';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint NOT IN ('FK_Notifications_EventGroup', 'FK_NotificationEvents_Notifications') THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "Notifications" SET "LastOccurredAt" = "CreatedAt" - INTERVAL '1 second';
        RAISE EXCEPTION 'Inbox accepted inverted occurrence timestamps';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'CK_Notifications_Occurrence' THEN RAISE; END IF;
    END;
END $inbox_integrity$;

ANALYZE "Notifications";
\o
EXPLAIN (ANALYZE, BUFFERS) EXECUTE phase_b_inbox(:owner_id, :profile_a, false, 25, 0);
EXPLAIN (ANALYZE, BUFFERS) EXECUTE phase_b_inbox(:owner_id, NULL, false, 25, 0);
DEALLOCATE phase_b_inbox;
DEALLOCATE phase_b_inbox_ensure;
DEALLOCATE phase_b_inbox_record;
ROLLBACK;
