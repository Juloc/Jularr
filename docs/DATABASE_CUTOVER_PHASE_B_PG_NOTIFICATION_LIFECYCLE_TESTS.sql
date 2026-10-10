BEGIN;
SET LOCAL statement_timeout = '20s';
\o /dev/null
\i /tmp/phase_b_notification_schedule_prepared.sql
\i /tmp/phase_b_notification_route_prepared.sql
INSERT INTO "UiLocales" ("Locale", "Name") VALUES ('phase-b-notification-life', 'Notification lifecycle') RETURNING "Id" AS locale_id \gset
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('phase-b-notification-life@example.invalid', 'Notification lifecycle', 1) RETURNING "Id" AS account_id \gset
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('phase-b-notification-other@example.invalid', 'Other notification lifecycle', 2) RETURNING "Id" AS other_account \gset
INSERT INTO "Profiles" ("OwnerAccountId", "DisplayName", "UiLocaleId", "TimeZone")
VALUES (:account_id, 'Notification lifecycle', :locale_id, 'Europe/Berlin') RETURNING "Id" AS profile_id \gset
INSERT INTO "Profiles" ("OwnerAccountId", "DisplayName", "UiLocaleId")
VALUES (:other_account, 'Other notification lifecycle', :locale_id) RETURNING "Id" AS other_profile \gset
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (:account_id, :profile_id), (:other_account, :other_profile);
INSERT INTO "AccountSessions" ("AccountId", "ActiveProfileId", "TokenHash", "ExpiresAt")
VALUES (:account_id, :profile_id, decode(repeat('01', 32), 'hex'), now() + INTERVAL '1 day') RETURNING "Id" AS session_id \gset
INSERT INTO "NotificationSchedules" ("ProfileId", "QuietHoursEnabled", "QuietHoursStart", "QuietHoursEnd", "DigestEnabled", "DigestLocalTime")
VALUES (:profile_id, true, '22:00', '08:00', true, '19:00');
INSERT INTO "NotificationDigestWeekdays" ("ProfileId", "IsoWeekday") SELECT :profile_id, day FROM generate_series(1, 7) AS day;
INSERT INTO "NotificationDigestChannels" ("ProfileId", "NotificationChannelTypeId") VALUES (:profile_id, 3);
INSERT INTO "NotificationSubscriptions" ("ProfileId", "NotificationEventCategoryTypeId", "IsEnabled", "NotificationTimingTypeId")
VALUES (:profile_id, 1, true, 2);
INSERT INTO "NotificationProfileChannels" ("ProfileId", "NotificationChannelTypeId", "IsEnabled") VALUES (:profile_id, 2, true), (:profile_id, 3, true);
INSERT INTO "NotificationSubscriptions" ("ProfileId", "NotificationEventCategoryTypeId", "IsEnabled", "NotificationTimingTypeId")
VALUES (:profile_id, 2, true, 1), (:profile_id, 8, true, 1);
INSERT INTO "NotificationSubscriptionChannels" ("ProfileId", "NotificationEventCategoryTypeId", "NotificationChannelTypeId")
VALUES (:profile_id, 1, 1), (:profile_id, 1, 2), (:profile_id, 1, 3), (:profile_id, 2, 3), (:profile_id, 8, 2);
INSERT INTO "NotificationDigestChannels" ("ProfileId", "NotificationChannelTypeId") VALUES (:profile_id, 2);
UPDATE "Accounts" SET "EmailVerifiedAt" = now() WHERE "Id" = :account_id;
INSERT INTO "NotificationPushEndpoints" ("ProfileId", "AccountId", "AccountSessionId", "TransportKey", "EndpointHash", "ProtectedSubscription")
VALUES (:profile_id, :account_id, :session_id, 'phase_b_fixture', decode(repeat('02', 32), 'hex'), decode('00', 'hex')) RETURNING "Id" AS endpoint_a \gset
INSERT INTO "NotificationPushEndpoints" ("ProfileId", "AccountId", "AccountSessionId", "TransportKey", "EndpointHash", "ProtectedSubscription")
VALUES (:profile_id, :account_id, :session_id, 'phase_b_fixture', decode(repeat('03', 32), 'hex'), decode('00', 'hex')) RETURNING "Id" AS endpoint_b \gset
INSERT INTO "Events" ("NotificationEventCategoryTypeId", "EventAudienceTypeId", "EventSeverityTypeId", "ProfileId", "MessageKey", "CreatedAt")
VALUES (1, 1, 1, :profile_id, 'notification_lifecycle', '2026-01-01 12:00Z') RETURNING "Id" AS event_id \gset
INSERT INTO "Events" ("NotificationEventCategoryTypeId", "EventAudienceTypeId", "EventSeverityTypeId", "ProfileId", "MessageKey", "CreatedAt")
VALUES (2, 1, 2, :profile_id, 'notification_lifecycle_failure', '2026-01-01 12:00Z') RETURNING "Id" AS failure_event \gset
INSERT INTO "Events" ("NotificationEventCategoryTypeId", "EventAudienceTypeId", "EventSeverityTypeId", "ProfileId", "MessageKey", "CreatedAt")
VALUES (2, 1, 2, :profile_id, 'notification_lifecycle_digest_forbidden', '2026-01-01 12:00Z');
INSERT INTO "Events" ("NotificationEventCategoryTypeId", "EventAudienceTypeId", "EventSeverityTypeId", "MessageKey", "CreatedAt")
VALUES (8, 2, 3, 'notification_lifecycle_admin', '2026-01-01 12:00Z') RETURNING "Id" AS admin_event \gset
INSERT INTO "NotificationDigestBatches" ("ProfileId", "ScheduledLocalDate", "ScheduledFor", "WindowEndAt")
VALUES (:profile_id, '2026-01-02', '2026-01-02 18:00Z', '2026-01-02 17:00Z') RETURNING "Id" AS batch_id \gset
INSERT INTO "NotificationExternalOccurrences" ("NotificationDigestBatchId", "ProfileId", "EventId", "NotificationEventCategoryTypeId")
VALUES (:batch_id, :profile_id, :'event_id', 1);
INSERT INTO "NotificationExternalOccurrences" ("ProfileId", "EventId", "NotificationEventCategoryTypeId")
VALUES (:profile_id, :'failure_event', 2);
INSERT INTO "Operations" ("OperationKindKey", "OperationStatusTypeId", "IdempotencyKey", "CreatedAt")
SELECT 'notification_delivery', 1, 'phase-b-notification-route-' || number::text, '2026-01-01 12:00Z' FROM generate_series(1, 6) AS number;
SELECT "Id" AS email_operation FROM "Operations" WHERE "IdempotencyKey" = 'phase-b-notification-route-1' \gset
SELECT "Id" AS push_a_operation FROM "Operations" WHERE "IdempotencyKey" = 'phase-b-notification-route-2' \gset
SELECT "Id" AS push_b_operation FROM "Operations" WHERE "IdempotencyKey" = 'phase-b-notification-route-3' \gset
SELECT "Id" AS immediate_operation FROM "Operations" WHERE "IdempotencyKey" = 'phase-b-notification-route-4' \gset
SELECT "Id" AS admin_operation FROM "Operations" WHERE "IdempotencyKey" = 'phase-b-notification-route-5' \gset
SELECT "Id" AS spare_operation FROM "Operations" WHERE "IdempotencyKey" = 'phase-b-notification-route-6' \gset
INSERT INTO "NotificationDeliveries" ("NotificationDigestBatchId", "ProfileId", "RecipientProfileId", "NotificationChannelTypeId", "OperationId", "CreatedAt")
VALUES (:batch_id, :profile_id, :profile_id, 3, :email_operation, '2026-01-01 12:00Z') RETURNING "Id" AS email_delivery \gset
INSERT INTO "NotificationDeliveries" (
    "NotificationDigestBatchId", "ProfileId", "RecipientProfileId", "NotificationChannelTypeId", "NotificationPushEndpointId", "OperationId", "CreatedAt")
VALUES (:batch_id, :profile_id, :profile_id, 2, :endpoint_a, :push_a_operation, '2026-01-01 12:00Z'),
       (:batch_id, :profile_id, :profile_id, 2, :endpoint_b, :push_b_operation, '2026-01-01 12:00Z');
SELECT "Id" AS push_a_delivery FROM "NotificationDeliveries" WHERE "OperationId" = :push_a_operation \gset
SELECT "Id" AS push_b_delivery FROM "NotificationDeliveries" WHERE "OperationId" = :push_b_operation \gset
INSERT INTO "NotificationDeliveries" ("EventId", "ProfileId", "RecipientProfileId", "NotificationChannelTypeId", "OperationId", "CreatedAt")
VALUES (:'failure_event', :profile_id, :profile_id, 3, :immediate_operation, '2026-01-01 12:00Z') RETURNING "Id" AS immediate_delivery \gset
INSERT INTO "NotificationDeliveries" ("EventId", "AccountId", "RecipientProfileId", "NotificationChannelTypeId", "NotificationPushEndpointId", "OperationId", "CreatedAt")
VALUES (:'admin_event', :account_id, :profile_id, 2, :endpoint_a, :admin_operation, '2026-01-01 12:00Z') RETURNING "Id" AS admin_delivery \gset
INSERT INTO "NotificationDeliveries" ("EventId", "ProfileId", "NotificationChannelTypeId", "CreatedAt")
VALUES (:'event_id', :profile_id, 1, '2026-01-01 12:00Z') RETURNING "Id" AS in_app_delivery \gset
SET CONSTRAINTS ALL IMMEDIATE;

EXECUTE phase_b_notification_route(:email_delivery, now(), ARRAY[3]::smallint[], ARRAY[]::text[]);
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
EXECUTE phase_b_notification_route(:email_delivery, now(), ARRAY[]::smallint[], ARRAY[]::text[]);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_notification_route(:push_a_delivery, now(), ARRAY[2]::smallint[], ARRAY[]::text[]);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_notification_route(:push_a_delivery, now(), ARRAY[2]::smallint[], ARRAY['phase_b_fixture']);
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
EXECUTE phase_b_notification_route(:admin_delivery, now(), ARRAY[2]::smallint[], ARRAY['phase_b_fixture']);
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
UPDATE "Accounts" SET "AccountRoleTypeId" = 2 WHERE "Id" = :account_id;
EXECUTE phase_b_notification_route(:admin_delivery, now(), ARRAY[2]::smallint[], ARRAY['phase_b_fixture']);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
UPDATE "Accounts" SET "AccountRoleTypeId" = 1, "EmailVerifiedAt" = NULL WHERE "Id" = :account_id;
EXECUTE phase_b_notification_route(:email_delivery, now(), ARRAY[3]::smallint[], ARRAY[]::text[]);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
UPDATE "Accounts" SET "EmailVerifiedAt" = now() WHERE "Id" = :account_id;
UPDATE "NotificationProfileChannels" SET "IsEnabled" = false WHERE "ProfileId" = :profile_id AND "NotificationChannelTypeId" = 2;
EXECUTE phase_b_notification_route(:push_a_delivery, now(), ARRAY[2]::smallint[], ARRAY['phase_b_fixture']);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
SELECT 1 / CASE WHEN COUNT(*) = 3 THEN 1 ELSE 0 END FROM "NotificationSubscriptionChannels"
WHERE "ProfileId" = :profile_id AND "NotificationEventCategoryTypeId" = 1;
UPDATE "NotificationProfileChannels" SET "IsEnabled" = true WHERE "ProfileId" = :profile_id AND "NotificationChannelTypeId" = 2;

EXECUTE phase_b_notification_schedule(:immediate_delivery, '2026-01-01 23:00Z') \gset
SELECT 1 / CASE WHEN :'NextAllowedAt'::timestamptz = '2026-01-02 07:00Z' THEN 1 ELSE 0 END;
EXECUTE phase_b_notification_schedule(:immediate_delivery, '2026-01-02 07:00Z') \gset
SELECT 1 / CASE WHEN :'NextAllowedAt'::timestamptz = '2026-01-02 07:00Z' THEN 1 ELSE 0 END;
EXECUTE phase_b_notification_schedule(:email_delivery, '2026-03-28 22:00Z') \gset
SELECT 1 / CASE WHEN :'NextAllowedAt'::timestamptz = '2026-03-29 06:00Z' THEN 1 ELSE 0 END;
EXECUTE phase_b_notification_schedule(:immediate_delivery, '2026-10-24 21:00Z') \gset
SELECT 1 / CASE WHEN :'NextAllowedAt'::timestamptz = '2026-10-25 07:00Z' THEN 1 ELSE 0 END;
EXECUTE phase_b_notification_schedule(:admin_delivery, '2026-01-01 23:00Z') \gset
SELECT 1 / CASE WHEN :'NextAllowedAt'::timestamptz = '2026-01-01 23:00Z' THEN 1 ELSE 0 END;
EXECUTE phase_b_notification_schedule(:in_app_delivery, '2026-01-01 23:00Z') \gset
SELECT 1 / CASE WHEN :'NextAllowedAt'::timestamptz = '2026-01-01 23:00Z' THEN 1 ELSE 0 END;
UPDATE "NotificationSchedules" SET "QuietHoursEnd" = '02:30' WHERE "ProfileId" = :profile_id;
EXECUTE phase_b_notification_schedule(:immediate_delivery, '2026-03-28 22:00Z') \gset
SELECT 1 / CASE WHEN :'NextAllowedAt'::timestamptz = '2026-03-29 01:30Z' THEN 1 ELSE 0 END;
EXECUTE phase_b_notification_schedule(:immediate_delivery, '2026-10-24 21:00Z') \gset
SELECT 1 / CASE WHEN :'NextAllowedAt'::timestamptz = '2026-10-25 01:30Z' THEN 1 ELSE 0 END;
UPDATE "NotificationSchedules" SET "QuietHoursStart" = '13:00', "QuietHoursEnd" = '15:00' WHERE "ProfileId" = :profile_id;
EXECUTE phase_b_notification_schedule(:immediate_delivery, '2026-06-01 12:00Z') \gset
SELECT 1 / CASE WHEN :'NextAllowedAt'::timestamptz = '2026-06-01 13:00Z' THEN 1 ELSE 0 END;
UPDATE "NotificationSchedules" SET "QuietHoursEnabled" = false WHERE "ProfileId" = :profile_id;
EXECUTE phase_b_notification_schedule(:immediate_delivery, '2026-06-01 12:00Z') \gset
SELECT 1 / CASE WHEN :'NextAllowedAt'::timestamptz = '2026-06-01 12:00Z' THEN 1 ELSE 0 END;

DO $notification_lifecycle_integrity$
DECLARE
    profile_id bigint;
    other_profile bigint;
    batch_id bigint;
    event_id uuid;
    empty_batch bigint;
    spare_operation bigint;
    rejected_constraint text;
BEGIN
    SELECT profile."Id" INTO profile_id FROM "Profiles" AS profile WHERE profile."DisplayName" = 'Notification lifecycle';
    SELECT profile."Id" INTO other_profile FROM "Profiles" AS profile WHERE profile."DisplayName" = 'Other notification lifecycle';
    SELECT batch."Id" INTO batch_id FROM "NotificationDigestBatches" AS batch WHERE batch."ProfileId" = profile_id;
    SELECT event."Id" INTO event_id FROM "Events" AS event WHERE event."MessageKey" = 'notification_lifecycle_digest_forbidden';
    SELECT operation."Id" INTO spare_operation FROM "Operations" AS operation WHERE operation."IdempotencyKey" = 'phase-b-notification-route-6';
    BEGIN
        UPDATE "Profiles" SET "TimeZone" = '+02:00' WHERE "Id" = profile_id;
        RAISE EXCEPTION 'Profile accepted a fixed-offset or unknown timezone';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'CK_Profiles_TimeZone' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "NotificationSchedules" SET "QuietHoursStart" = '08:00', "QuietHoursEnd" = '08:00' WHERE "ProfileId" = profile_id;
        RAISE EXCEPTION 'Quiet Hours accepted an implicit all-day mute';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'CK_NotificationSchedules_QuietHours' THEN RAISE; END IF;
    END;
    BEGIN
        DELETE FROM "NotificationDigestChannels" WHERE "ProfileId" = profile_id;
        RAISE EXCEPTION 'Enabled Digest lost its final external channel';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'CK_NotificationSchedules_DigestRoutes' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO "NotificationDigestBatches" ("ProfileId", "ScheduledLocalDate", "ScheduledFor", "WindowEndAt")
        VALUES (profile_id, '2026-01-03', '2026-01-03 18:00Z', '2026-01-03 17:00Z') RETURNING "Id" INTO empty_batch;
        INSERT INTO "NotificationDeliveries" ("NotificationDigestBatchId", "ProfileId", "RecipientProfileId", "NotificationChannelTypeId", "OperationId")
        VALUES (empty_batch, profile_id, profile_id, 3, spare_operation);
        RAISE EXCEPTION 'Empty Digest received a durable send route';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'CK_NotificationDigestBatches_CapturedEvents' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO "NotificationDeliveries" ("EventId", "ProfileId", "RecipientProfileId", "NotificationChannelTypeId", "OperationId")
        SELECT item."EventId", profile_id, profile_id, 3, spare_operation
        FROM "NotificationExternalOccurrences" AS item WHERE item."NotificationDigestBatchId" = batch_id;
        RAISE EXCEPTION 'Captured Digest occurrence also received an immediate external route';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'FK_NotificationDeliveries_ImmediateOccurrence' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "NotificationSchedules" SET "DigestEnabled" = false WHERE "ProfileId" = profile_id;
        RAISE EXCEPTION 'Digest was disabled without resolving event preferences';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'CK_NotificationSubscriptions_DigestSchedule' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO "NotificationExternalOccurrences" ("NotificationDigestBatchId", "ProfileId", "EventId", "NotificationEventCategoryTypeId")
        VALUES (batch_id, profile_id, event_id, 2);
        RAISE EXCEPTION 'Failure event entered Digest against canonical policy';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'FK_NotificationExternalOccurrences_DigestPolicy' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO "NotificationExternalOccurrences" ("NotificationDigestBatchId", "ProfileId", "EventId", "NotificationEventCategoryTypeId")
        VALUES (batch_id, other_profile, event_id, 2);
        RAISE EXCEPTION 'Foreign-profile event entered Digest';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'FK_NotificationExternalOccurrences_BatchProfile' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "NotificationDeliveries" SET "RecipientProfileId" = other_profile WHERE "ProfileId" = profile_id AND "NotificationChannelTypeId" = 3;
        RAISE EXCEPTION 'Delivery changed its profile scope';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'CK_NotificationDeliveries_ExternalOperation' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "NotificationDeliveries" SET "CancellationCode" = 'cancelled_without_time' WHERE "ProfileId" = profile_id;
        RAISE EXCEPTION 'Cancellation accepted without its terminal timestamp';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected_constraint = CONSTRAINT_NAME;
        IF rejected_constraint <> 'CK_NotificationDeliveries_Cancellation' THEN RAISE; END IF;
    END;
END $notification_lifecycle_integrity$;

UPDATE "NotificationDeliveries" SET "DeliveredAt" = '2026-01-02 19:00Z' WHERE "Id" = :email_delivery;
UPDATE "Operations" SET "OperationStatusTypeId" = 3, "FinishedAt" = '2026-01-02 19:00Z' WHERE "Id" = :email_operation;
SELECT 1 / CASE WHEN COUNT(*) = 2 THEN 1 ELSE 0 END FROM "NotificationDeliveries"
WHERE "NotificationDigestBatchId" = :batch_id AND "DeliveredAt" IS NULL;
UPDATE "NotificationPushEndpoints" SET "RevokedAt" = now() WHERE "Id" = :endpoint_a;
EXECUTE phase_b_notification_route(:push_a_delivery, now(), ARRAY[2]::smallint[], ARRAY['phase_b_fixture']);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_notification_route(:push_b_delivery, now(), ARRAY[2]::smallint[], ARRAY['phase_b_fixture']);
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
UPDATE "AccountSessions" SET "RevokedAt" = now() WHERE "Id" = :session_id;
EXECUTE phase_b_notification_route(:push_b_delivery, now(), ARRAY[2]::smallint[], ARRAY['phase_b_fixture']);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
SELECT 1 / CASE WHEN COUNT(*) = 1 THEN 1 ELSE 0 END FROM "NotificationExternalOccurrences" WHERE "NotificationDigestBatchId" = :batch_id;
SET CONSTRAINTS ALL DEFERRED;
UPDATE "NotificationSubscriptions" SET "NotificationTimingTypeId" = 1 WHERE "ProfileId" = :profile_id;
UPDATE "NotificationSchedules" SET "DigestEnabled" = false WHERE "ProfileId" = :profile_id;
SET CONSTRAINTS ALL IMMEDIATE;
EXECUTE phase_b_notification_route(:email_delivery, now(), ARRAY[3]::smallint[], ARRAY[]::text[]);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
SELECT 1 / CASE WHEN "DigestLocalTime" = '19:00' AND "QuietHoursStart" = '13:00' AND "QuietHoursEnd" = '15:00' THEN 1 ELSE 0 END
FROM "NotificationSchedules" WHERE "ProfileId" = :profile_id;
DEALLOCATE phase_b_notification_schedule;
DEALLOCATE phase_b_notification_route;
\o
ROLLBACK;
