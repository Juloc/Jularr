BEGIN;

ALTER TABLE "Profiles"
    ADD COLUMN "TimeZone" varchar(100) NOT NULL DEFAULT 'Etc/UTC';
CREATE FUNCTION "ValidateProfileTimeZone"() RETURNS trigger
LANGUAGE plpgsql AS $profile_time_zone$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_timezone_names AS zone
        WHERE zone.name = NEW."TimeZone" AND (zone.name = 'UTC' OR POSITION('/' IN zone.name) > 0)
            AND zone.name NOT LIKE 'posix/%' AND zone.name NOT LIKE 'right/%'
    ) THEN
        RAISE EXCEPTION 'Profile timezone must be a recognized canonical timezone'
            USING ERRCODE = '23514', CONSTRAINT = 'CK_Profiles_TimeZone';
    END IF;
    RETURN NEW;
END $profile_time_zone$;
CREATE TRIGGER "TR_Profiles_TimeZone"
BEFORE INSERT OR UPDATE OF "TimeZone" ON "Profiles"
FOR EACH ROW EXECUTE FUNCTION "ValidateProfileTimeZone"();

CREATE TABLE "NotificationSchedules" (
    "ProfileId" bigint NOT NULL,
    "QuietHoursEnabled" boolean NOT NULL DEFAULT false,
    "QuietHoursStart" time(0),
    "QuietHoursEnd" time(0),
    "DigestEnabled" boolean NOT NULL DEFAULT false,
    "DigestLocalTime" time(0),
    "PopupsEnabled" boolean NOT NULL DEFAULT true,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_NotificationSchedules" PRIMARY KEY ("ProfileId"),
    CONSTRAINT "FK_NotificationSchedules_Profiles" FOREIGN KEY ("ProfileId") REFERENCES "Profiles" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "CK_NotificationSchedules_QuietHours" CHECK (
        ("QuietHoursStart" IS NULL) = ("QuietHoursEnd" IS NULL)
        AND (NOT "QuietHoursEnabled" OR "QuietHoursStart" IS NOT NULL)
        AND ("QuietHoursStart" IS NULL OR ("QuietHoursStart" <> "QuietHoursEnd"
            AND "QuietHoursStart" < '24:00'::time AND "QuietHoursEnd" < '24:00'::time))),
    CONSTRAINT "CK_NotificationSchedules_DigestTime" CHECK (
        (NOT "DigestEnabled" OR "DigestLocalTime" IS NOT NULL)
        AND ("DigestLocalTime" IS NULL OR "DigestLocalTime" < '24:00'::time))
);
CREATE TABLE "NotificationDigestWeekdays" (
    "ProfileId" bigint NOT NULL,
    "IsoWeekday" smallint NOT NULL,
    CONSTRAINT "PK_NotificationDigestWeekdays" PRIMARY KEY ("ProfileId", "IsoWeekday"),
    CONSTRAINT "FK_NotificationDigestWeekdays_Schedules" FOREIGN KEY ("ProfileId")
        REFERENCES "NotificationSchedules" ("ProfileId") ON DELETE RESTRICT,
    CONSTRAINT "CK_NotificationDigestWeekdays_Day" CHECK ("IsoWeekday" BETWEEN 1 AND 7)
);
CREATE TABLE "NotificationDigestChannels" (
    "ProfileId" bigint NOT NULL,
    "NotificationChannelTypeId" smallint NOT NULL,
    CONSTRAINT "PK_NotificationDigestChannels" PRIMARY KEY ("ProfileId", "NotificationChannelTypeId"),
    CONSTRAINT "FK_NotificationDigestChannels_Schedules" FOREIGN KEY ("ProfileId")
        REFERENCES "NotificationSchedules" ("ProfileId") ON DELETE RESTRICT,
    CONSTRAINT "FK_NotificationDigestChannels_Channel" FOREIGN KEY ("NotificationChannelTypeId")
        REFERENCES "NotificationChannelTypes" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "CK_NotificationDigestChannels_External" CHECK ("NotificationChannelTypeId" IN (2, 3))
);

CREATE FUNCTION "ValidateNotificationDigestSchedule"() RETURNS trigger
LANGUAGE plpgsql AS $digest_schedule$
DECLARE
    profile_ids bigint[];
    profile_id bigint;
BEGIN
    IF TG_OP = 'INSERT' THEN
        profile_ids := ARRAY[NEW."ProfileId"];
    ELSIF TG_OP = 'DELETE' THEN
        profile_ids := ARRAY[OLD."ProfileId"];
    ELSE
        profile_ids := ARRAY[OLD."ProfileId", NEW."ProfileId"];
    END IF;
    FOREACH profile_id IN ARRAY profile_ids
    LOOP
        IF EXISTS (
            SELECT 1 FROM "NotificationSubscriptions" AS subscription
            LEFT JOIN "NotificationSchedules" AS schedule ON schedule."ProfileId" = subscription."ProfileId"
            WHERE subscription."ProfileId" = profile_id AND subscription."IsEnabled" AND subscription."NotificationTimingTypeId" = 2
                AND schedule."DigestEnabled" IS DISTINCT FROM TRUE
        ) THEN
            RAISE EXCEPTION 'Enabled Digest preference requires enabled profile Digest configuration'
                USING ERRCODE = '23514', CONSTRAINT = 'CK_NotificationSubscriptions_DigestSchedule';
        END IF;
        IF EXISTS (
            SELECT 1 FROM "NotificationSubscriptions" AS subscription
            INNER JOIN "NotificationEventCategoryTypes" AS category ON category."Id" = subscription."NotificationEventCategoryTypeId"
            WHERE subscription."ProfileId" = profile_id AND subscription."NotificationTimingTypeId" = 2 AND NOT category."AllowsDigest"
        ) THEN
            RAISE EXCEPTION 'Event category cannot use Digest timing'
                USING ERRCODE = '23514', CONSTRAINT = 'CK_NotificationSubscriptions_DigestPolicy';
        END IF;
        IF EXISTS (
            SELECT 1 FROM "NotificationSchedules" AS schedule
            WHERE schedule."ProfileId" = profile_id AND schedule."DigestEnabled"
                AND (NOT EXISTS (SELECT 1 FROM "NotificationDigestWeekdays" AS day WHERE day."ProfileId" = profile_id)
                     OR NOT EXISTS (SELECT 1 FROM "NotificationDigestChannels" AS channel WHERE channel."ProfileId" = profile_id))
        ) THEN
            RAISE EXCEPTION 'Enabled Digest requires at least one weekday and external channel'
                USING ERRCODE = '23514', CONSTRAINT = 'CK_NotificationSchedules_DigestRoutes';
        END IF;
    END LOOP;
    RETURN NULL;
END $digest_schedule$;
CREATE CONSTRAINT TRIGGER "TR_NotificationSchedules_DigestRoutes"
AFTER INSERT OR UPDATE ON "NotificationSchedules" DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION "ValidateNotificationDigestSchedule"();
CREATE CONSTRAINT TRIGGER "TR_NotificationSubscriptions_DigestSchedule"
AFTER INSERT OR UPDATE OR DELETE ON "NotificationSubscriptions" DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION "ValidateNotificationDigestSchedule"();
CREATE CONSTRAINT TRIGGER "TR_NotificationDigestWeekdays_DigestRoutes"
AFTER INSERT OR UPDATE OR DELETE ON "NotificationDigestWeekdays" DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION "ValidateNotificationDigestSchedule"();
CREATE CONSTRAINT TRIGGER "TR_NotificationDigestChannels_DigestRoutes"
AFTER INSERT OR UPDATE OR DELETE ON "NotificationDigestChannels" DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION "ValidateNotificationDigestSchedule"();

ALTER TABLE "AccountSessions" ADD CONSTRAINT "UX_AccountSessions_Id_Account" UNIQUE ("Id", "AccountId");
CREATE TABLE "NotificationPushEndpoints" (
    "Id" bigint GENERATED BY DEFAULT AS IDENTITY,
    "PublicId" uuid NOT NULL DEFAULT gen_random_uuid(),
    "ProfileId" bigint NOT NULL,
    "AccountId" bigint NOT NULL,
    "AccountSessionId" bigint,
    "TransportKey" varchar(80) NOT NULL,
    "EndpointHash" bytea NOT NULL,
    "ProtectedSubscription" bytea NOT NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    "RevokedAt" timestamptz,
    CONSTRAINT "PK_NotificationPushEndpoints" PRIMARY KEY ("Id"),
    CONSTRAINT "UX_NotificationPushEndpoints_PublicId" UNIQUE ("PublicId"),
    CONSTRAINT "UX_NotificationPushEndpoints_Id_Profile" UNIQUE ("Id", "ProfileId"),
    CONSTRAINT "UX_NotificationPushEndpoints_Id_Account" UNIQUE ("Id", "AccountId"),
    CONSTRAINT "UX_NotificationPushEndpoints_Profile_Endpoint" UNIQUE ("ProfileId", "TransportKey", "EndpointHash"),
    CONSTRAINT "FK_NotificationPushEndpoints_Profiles" FOREIGN KEY ("ProfileId") REFERENCES "Profiles" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_NotificationPushEndpoints_Accounts" FOREIGN KEY ("AccountId") REFERENCES "Accounts" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_NotificationPushEndpoints_SessionAccount" FOREIGN KEY ("AccountSessionId", "AccountId")
        REFERENCES "AccountSessions" ("Id", "AccountId") ON DELETE RESTRICT,
    CONSTRAINT "CK_NotificationPushEndpoints_Transport" CHECK (length(btrim("TransportKey")) > 0 AND "TransportKey" = btrim("TransportKey")),
    CONSTRAINT "CK_NotificationPushEndpoints_Hash" CHECK (octet_length("EndpointHash") = 32),
    CONSTRAINT "CK_NotificationPushEndpoints_Protected" CHECK (octet_length("ProtectedSubscription") BETWEEN 1 AND 16384),
    CONSTRAINT "CK_NotificationPushEndpoints_Revoked" CHECK ("RevokedAt" IS NULL OR "RevokedAt" >= "CreatedAt")
);

ALTER TABLE "NotificationEventCategoryTypes"
    ADD COLUMN "AllowsDigest" boolean NOT NULL DEFAULT false,
    ADD COLUMN "BypassesQuietHours" boolean NOT NULL DEFAULT false,
    ADD CONSTRAINT "UX_NotificationEventCategoryTypes_DigestPolicy" UNIQUE ("Id", "AllowsDigest");
UPDATE "NotificationEventCategoryTypes" SET "AllowsDigest" = true WHERE "Id" IN (1, 3, 5, 6, 7);
UPDATE "NotificationEventCategoryTypes" SET "BypassesQuietHours" = true WHERE "Id" = 8;
ALTER TABLE "Events" ADD CONSTRAINT "UX_Events_Id_Profile_Category" UNIQUE ("Id", "ProfileId", "NotificationEventCategoryTypeId");

CREATE TABLE "NotificationDigestBatches" (
    "Id" bigint GENERATED BY DEFAULT AS IDENTITY,
    "ProfileId" bigint NOT NULL,
    "ScheduledLocalDate" date NOT NULL,
    "ScheduledFor" timestamptz NOT NULL,
    "WindowEndAt" timestamptz NOT NULL,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_NotificationDigestBatches" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_NotificationDigestBatches_Profiles" FOREIGN KEY ("ProfileId") REFERENCES "Profiles" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "UX_NotificationDigestBatches_Profile_Date" UNIQUE ("ProfileId", "ScheduledLocalDate"),
    CONSTRAINT "UX_NotificationDigestBatches_Id_Profile" UNIQUE ("Id", "ProfileId"),
    CONSTRAINT "CK_NotificationDigestBatches_Window" CHECK ("WindowEndAt" <= "ScheduledFor")
);
CREATE TABLE "NotificationExternalOccurrences" (
    "NotificationDigestBatchId" bigint,
    "ProfileId" bigint NOT NULL,
    "EventId" uuid NOT NULL,
    "NotificationEventCategoryTypeId" smallint NOT NULL,
    "AllowsDigest" boolean GENERATED ALWAYS AS (CASE WHEN "NotificationDigestBatchId" IS NOT NULL THEN true ELSE NULL::boolean END) STORED,
    "ImmediateEventId" uuid GENERATED ALWAYS AS (CASE WHEN "NotificationDigestBatchId" IS NULL THEN "EventId" ELSE NULL::uuid END) STORED,
    CONSTRAINT "PK_NotificationExternalOccurrences" PRIMARY KEY ("ProfileId", "EventId"),
    CONSTRAINT "UX_NotificationExternalOccurrences_Batch_Event" UNIQUE ("NotificationDigestBatchId", "EventId"),
    CONSTRAINT "UX_NotificationExternalOccurrences_ImmediateEvent" UNIQUE ("ImmediateEventId", "ProfileId"),
    CONSTRAINT "FK_NotificationExternalOccurrences_BatchProfile" FOREIGN KEY ("NotificationDigestBatchId", "ProfileId")
        REFERENCES "NotificationDigestBatches" ("Id", "ProfileId") ON DELETE RESTRICT,
    CONSTRAINT "FK_NotificationExternalOccurrences_EventProfileCategory" FOREIGN KEY ("EventId", "ProfileId", "NotificationEventCategoryTypeId")
        REFERENCES "Events" ("Id", "ProfileId", "NotificationEventCategoryTypeId") ON DELETE RESTRICT,
    CONSTRAINT "FK_NotificationExternalOccurrences_DigestPolicy" FOREIGN KEY ("NotificationEventCategoryTypeId", "AllowsDigest")
        REFERENCES "NotificationEventCategoryTypes" ("Id", "AllowsDigest") ON DELETE RESTRICT
);

-- Scheduling/claim/retry status stays on the linked Operations row, never a second queue.
ALTER TABLE "NotificationDeliveries"
    ALTER COLUMN "EventId" DROP NOT NULL,
    ADD COLUMN "NotificationDigestBatchId" bigint,
    ADD COLUMN "NotificationPushEndpointId" bigint,
    ADD COLUMN "RecipientProfileId" bigint,
    ADD COLUMN "DirectEventId" uuid GENERATED ALWAYS AS (CASE WHEN "NotificationChannelTypeId" <> 1 THEN "EventId" ELSE NULL::uuid END) STORED,
    ADD COLUMN "CancelledAt" timestamptz,
    ADD COLUMN "CancellationCode" varchar(80),
    DROP CONSTRAINT "UX_NotificationDeliveries_Event_Recipient_Channel",
    ADD CONSTRAINT "UX_NotificationDeliveries_Occurrence_Route" UNIQUE NULLS NOT DISTINCT (
        "EventId", "NotificationDigestBatchId", "ProfileId", "AccountId", "NotificationChannelTypeId", "NotificationPushEndpointId"),
    ADD CONSTRAINT "FK_NotificationDeliveries_BatchProfile" FOREIGN KEY ("NotificationDigestBatchId", "ProfileId")
        REFERENCES "NotificationDigestBatches" ("Id", "ProfileId") ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_NotificationDeliveries_EndpointProfile" FOREIGN KEY ("NotificationPushEndpointId", "ProfileId")
        REFERENCES "NotificationPushEndpoints" ("Id", "ProfileId") ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_NotificationDeliveries_RecipientProfile" FOREIGN KEY ("RecipientProfileId")
        REFERENCES "Profiles" ("Id") ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_NotificationDeliveries_EndpointRecipient" FOREIGN KEY ("NotificationPushEndpointId", "RecipientProfileId")
        REFERENCES "NotificationPushEndpoints" ("Id", "ProfileId") ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_NotificationDeliveries_EndpointAccount" FOREIGN KEY ("NotificationPushEndpointId", "AccountId")
        REFERENCES "NotificationPushEndpoints" ("Id", "AccountId") ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_NotificationDeliveries_ImmediateOccurrence" FOREIGN KEY ("DirectEventId", "ProfileId")
        REFERENCES "NotificationExternalOccurrences" ("ImmediateEventId", "ProfileId") ON DELETE RESTRICT,
    ADD CONSTRAINT "CK_NotificationDeliveries_Occurrence" CHECK (
        num_nonnulls("EventId", "NotificationDigestBatchId") = 1
        AND ("NotificationDigestBatchId" IS NULL OR ("ProfileId" IS NOT NULL AND "NotificationChannelTypeId" IN (2, 3)))),
    ADD CONSTRAINT "CK_NotificationDeliveries_PushEndpoint" CHECK (
        ("NotificationChannelTypeId" = 2 AND "NotificationPushEndpointId" IS NOT NULL AND "RecipientProfileId" IS NOT NULL)
        OR ("NotificationChannelTypeId" <> 2 AND "NotificationPushEndpointId" IS NULL)),
    ADD CONSTRAINT "CK_NotificationDeliveries_ExternalOperation" CHECK (
        ("NotificationChannelTypeId" = 1 AND "RecipientProfileId" IS NULL)
        OR ("NotificationChannelTypeId" <> 1 AND "OperationId" IS NOT NULL AND "RecipientProfileId" IS NOT NULL
            AND ("ProfileId" IS NULL OR "RecipientProfileId" = "ProfileId"))),
    ADD CONSTRAINT "CK_NotificationDeliveries_Cancellation" CHECK (
        ("CancelledAt" IS NULL AND "CancellationCode" IS NULL)
        OR ("CancelledAt" IS NOT NULL AND "DeliveredAt" IS NULL AND "CancellationCode" IS NOT NULL
            AND length(btrim("CancellationCode")) > 0 AND "CancelledAt" >= "CreatedAt"));

CREATE FUNCTION "ValidateNotificationDigestBatch"() RETURNS trigger
LANGUAGE plpgsql AS $digest_batch$
DECLARE
    batch_ids bigint[];
    batch_id bigint;
BEGIN
    IF TG_TABLE_NAME = 'NotificationDigestBatches' THEN
        batch_ids := ARRAY[NEW."Id"];
    ELSIF TG_OP = 'INSERT' THEN
        batch_ids := ARRAY[NEW."NotificationDigestBatchId"];
    ELSIF TG_OP = 'DELETE' THEN
        batch_ids := ARRAY[OLD."NotificationDigestBatchId"];
    ELSE
        batch_ids := ARRAY[OLD."NotificationDigestBatchId", NEW."NotificationDigestBatchId"];
    END IF;
    FOREACH batch_id IN ARRAY batch_ids
    LOOP
        IF batch_id IS NULL THEN CONTINUE; END IF;
        IF EXISTS (
            SELECT 1 FROM "NotificationDeliveries" AS delivery
            WHERE delivery."NotificationDigestBatchId" = batch_id
                AND NOT EXISTS (SELECT 1 FROM "NotificationExternalOccurrences" AS item WHERE item."NotificationDigestBatchId" = batch_id)
        ) OR EXISTS (
            SELECT 1 FROM "NotificationExternalOccurrences" AS item
            INNER JOIN "NotificationDigestBatches" AS batch ON batch."Id" = item."NotificationDigestBatchId"
            INNER JOIN "Events" AS event ON event."Id" = item."EventId"
            WHERE item."NotificationDigestBatchId" = batch_id AND event."CreatedAt" > batch."WindowEndAt"
        ) THEN
            RAISE EXCEPTION 'Digest delivery requires captured events inside its frozen window'
                USING ERRCODE = '23514', CONSTRAINT = 'CK_NotificationDigestBatches_CapturedEvents';
        END IF;
    END LOOP;
    RETURN NULL;
END $digest_batch$;
CREATE CONSTRAINT TRIGGER "TR_NotificationDeliveries_DigestEvents"
AFTER INSERT OR UPDATE ON "NotificationDeliveries" DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION "ValidateNotificationDigestBatch"();
CREATE CONSTRAINT TRIGGER "TR_NotificationDigestBatches_CaptureWindow"
AFTER UPDATE ON "NotificationDigestBatches" DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION "ValidateNotificationDigestBatch"();
CREATE CONSTRAINT TRIGGER "TR_NotificationExternalOccurrences_CaptureWindow"
AFTER INSERT OR UPDATE OR DELETE ON "NotificationExternalOccurrences" DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION "ValidateNotificationDigestBatch"();

CREATE INDEX "IX_NotificationPushEndpoints_Account" ON "NotificationPushEndpoints" ("AccountId", "Id");
CREATE INDEX "IX_NotificationPushEndpoints_Session" ON "NotificationPushEndpoints" ("AccountSessionId", "Id")
    WHERE "AccountSessionId" IS NOT NULL;
CREATE INDEX "IX_NotificationExternalOccurrences_Event" ON "NotificationExternalOccurrences" ("EventId", "NotificationDigestBatchId");
CREATE INDEX "IX_NotificationDeliveries_Batch" ON "NotificationDeliveries" ("NotificationDigestBatchId", "Id")
    WHERE "NotificationDigestBatchId" IS NOT NULL;
CREATE INDEX "IX_NotificationDeliveries_Endpoint" ON "NotificationDeliveries" ("NotificationPushEndpointId", "Id")
    WHERE "NotificationPushEndpointId" IS NOT NULL;
COMMIT;
