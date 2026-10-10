-- Phase B working draft 11: canonical domain event ledger and delivery diagnostics.
-- Source: existing EventLogStore/JularrEventModels and approved admin-notifications/SPEC.md.
-- Apply ONLY after Parts 01..10 to a NEW disposable PostgreSQL scratch database.
-- No second scheduler/queue: long-running or retryable dispatch uses Operations.
-- Event/category byte codes and occurrence policy are fixed by the B01 target contract.
BEGIN;

CREATE TABLE "EventAudienceTypes" (
    "Id" smallint NOT NULL,
    "Key" text NOT NULL,
    "RequiresProfile" boolean NOT NULL,
    CONSTRAINT "PK_EventAudienceTypes" PRIMARY KEY ("Id"),
    CONSTRAINT "UX_EventAudienceTypes_Key" UNIQUE ("Key"),
    CONSTRAINT "UX_EventAudienceTypes_Id_RequiresProfile" UNIQUE ("Id","RequiresProfile"),
    CONSTRAINT "CK_EventAudienceTypes_Id" CHECK ("Id" BETWEEN 0 AND 255),
    CONSTRAINT "CK_EventAudienceTypes_Key" CHECK (length(btrim("Key")) > 0)
);

CREATE TABLE "EventSeverityTypes" (
    "Id" smallint NOT NULL,
    "Key" text NOT NULL,
    CONSTRAINT "PK_EventSeverityTypes" PRIMARY KEY ("Id"),
    CONSTRAINT "UX_EventSeverityTypes_Key" UNIQUE ("Key"),
    CONSTRAINT "CK_EventSeverityTypes_Id" CHECK ("Id" BETWEEN 0 AND 255),
    CONSTRAINT "CK_EventSeverityTypes_Key" CHECK (length(btrim("Key")) > 0)
);

INSERT INTO "EventAudienceTypes" ("Id", "Key", "RequiresProfile")
VALUES
    (1, 'profile', true),
    (2, 'admin', false);
INSERT INTO "EventSeverityTypes" ("Id", "Key")
VALUES
    (1, 'info'),
    (2, 'warning'),
    (3, 'critical');

ALTER TABLE "NotificationEventCategoryTypes"
    ADD CONSTRAINT "FK_NotificationEventCategoryTypes_EventAudienceTypes"
        FOREIGN KEY ("EventAudienceTypeId") REFERENCES "EventAudienceTypes" ("Id") ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_NotificationEventCategoryTypes_EventSeverityTypes"
        FOREIGN KEY ("EventSeverityTypeId") REFERENCES "EventSeverityTypes" ("Id") ON DELETE RESTRICT;

-- Event Id is public/distributed for replay protection; UUID is a documented
-- exception to ordinary internal bigint identities. This is the sole event log.
-- MessageParams are localization parameters, not arbitrary persisted business state.
CREATE TABLE "Events" (
    "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
    "NotificationEventCategoryTypeId" smallint NOT NULL,
    "EventAudienceTypeId" smallint NOT NULL,
    "EventSeverityTypeId" smallint NOT NULL,
    "ProfileId" bigint,
    "HasProfileAudience" boolean GENERATED ALWAYS AS ("ProfileId" IS NOT NULL) STORED,
    "WorkId" bigint,
    "RelatedOperationId" bigint,
    "MessageKey" text NOT NULL,
    "MessageParams" jsonb NOT NULL DEFAULT '{}'::jsonb,
    "DeepLink" text,
    "DedupKey" text,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_Events" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_Events_CategoryPolicy" FOREIGN KEY ("NotificationEventCategoryTypeId", "EventAudienceTypeId", "EventSeverityTypeId")
        REFERENCES "NotificationEventCategoryTypes" ("Id", "EventAudienceTypeId", "EventSeverityTypeId") ON DELETE RESTRICT,
    CONSTRAINT "FK_Events_EventAudienceTypes" FOREIGN KEY ("EventAudienceTypeId","HasProfileAudience")
        REFERENCES "EventAudienceTypes" ("Id","RequiresProfile") ON DELETE RESTRICT,
    CONSTRAINT "FK_Events_EventSeverityTypes" FOREIGN KEY ("EventSeverityTypeId")
        REFERENCES "EventSeverityTypes" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_Events_Profiles" FOREIGN KEY ("ProfileId")
        REFERENCES "Profiles" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_Events_Works" FOREIGN KEY ("WorkId")
        REFERENCES "Works" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_Events_Operations" FOREIGN KEY ("RelatedOperationId")
        REFERENCES "Operations" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "UX_Events_Id_ProfileId" UNIQUE ("Id","ProfileId"),
    CONSTRAINT "UX_Events_Id_Audience" UNIQUE ("Id","HasProfileAudience"),
    CONSTRAINT "CK_Events_MessageKey" CHECK (length(btrim("MessageKey")) BETWEEN 1 AND 240),
    CONSTRAINT "CK_Events_MessageParams" CHECK (
        jsonb_typeof("MessageParams") = 'object' AND octet_length("MessageParams"::text) <= 16384),
    CONSTRAINT "CK_Events_DeepLink" CHECK ("DeepLink" IS NULL OR length("DeepLink") <= 400),
    CONSTRAINT "CK_Events_DedupKey" CHECK ("DedupKey" IS NULL OR length("DedupKey") <= 200)
);

-- The existing 'Notifications' draft in 02 is the recipient-specific IN-APP
-- inbox, not a second event log. Admin-target messages belong to Accounts and
-- Profile-target messages to Profiles. No duplicated localized message body.
ALTER TABLE "Notifications"
    DROP COLUMN "MessageKey",
    DROP COLUMN "Data",
    ALTER COLUMN "AccountId" DROP NOT NULL,
    ADD COLUMN "ProfileId" bigint,
    ADD COLUMN "EventId" uuid NOT NULL,
    ADD COLUMN "HasProfileRecipient" boolean GENERATED ALWAYS AS ("ProfileId" IS NOT NULL) STORED,
    ADD CONSTRAINT "FK_Notifications_Profiles"
        FOREIGN KEY ("ProfileId") REFERENCES "Profiles" ("Id") ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_Notifications_Events"
        FOREIGN KEY ("EventId") REFERENCES "Events" ("Id") ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_Notifications_ProfileEvents"
        FOREIGN KEY ("EventId","ProfileId") REFERENCES "Events" ("Id","ProfileId") ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_Notifications_EventsAudience"
        FOREIGN KEY ("EventId","HasProfileRecipient") REFERENCES "Events" ("Id","HasProfileAudience") ON DELETE RESTRICT,
    ADD CONSTRAINT "CK_Notifications_ExactlyOneRecipient"
        CHECK (num_nonnulls("ProfileId","AccountId") = 1),
    ADD CONSTRAINT "UX_Notifications_Event_Recipient"
        UNIQUE NULLS NOT DISTINCT ("EventId","ProfileId","AccountId");

-- One recipient + one actual channel = one delivery target.
-- Operations is the ONLY durable claim/retry queue. In-app may be delivered
-- inline after commit; external channels must not be presented as available
-- until a real sink is installed/configured. No per-delivery lease/worker loop.
CREATE TABLE "NotificationDeliveries" (
    "Id" bigint GENERATED BY DEFAULT AS IDENTITY,
    "EventId" uuid NOT NULL,
    "ProfileId" bigint,
    "AccountId" bigint,
    "HasProfileRecipient" boolean GENERATED ALWAYS AS ("ProfileId" IS NOT NULL) STORED,
    "NotificationChannelTypeId" smallint NOT NULL,
    "OperationId" bigint,
    "CreatedAt" timestamptz NOT NULL DEFAULT now(),
    "DeliveredAt" timestamptz,
    CONSTRAINT "PK_NotificationDeliveries" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_NotificationDeliveries_Events" FOREIGN KEY ("EventId")
        REFERENCES "Events" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_NotificationDeliveries_ProfileEvents" FOREIGN KEY ("EventId","ProfileId")
        REFERENCES "Events" ("Id","ProfileId") ON DELETE RESTRICT,
    CONSTRAINT "FK_NotificationDeliveries_EventsAudience" FOREIGN KEY ("EventId","HasProfileRecipient")
        REFERENCES "Events" ("Id","HasProfileAudience") ON DELETE RESTRICT,
    CONSTRAINT "FK_NotificationDeliveries_Profiles" FOREIGN KEY ("ProfileId")
        REFERENCES "Profiles" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_NotificationDeliveries_Accounts" FOREIGN KEY ("AccountId")
        REFERENCES "Accounts" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_NotificationDeliveries_NotificationChannelTypes" FOREIGN KEY ("NotificationChannelTypeId")
        REFERENCES "NotificationChannelTypes" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_NotificationDeliveries_Operations" FOREIGN KEY ("OperationId")
        REFERENCES "Operations" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "UX_NotificationDeliveries_Event_Recipient_Channel"
        UNIQUE NULLS NOT DISTINCT ("EventId","ProfileId","AccountId","NotificationChannelTypeId"),
    CONSTRAINT "UX_NotificationDeliveries_OperationId" UNIQUE ("OperationId"),
    CONSTRAINT "CK_NotificationDeliveries_ExactlyOneRecipient"
        CHECK (num_nonnulls("ProfileId","AccountId") = 1),
    CONSTRAINT "CK_NotificationDeliveries_Time"
        CHECK ("DeliveredAt" IS NULL OR "DeliveredAt" >= "CreatedAt")
);

-- Append-only technical result for each real delivery attempt.
-- No raw exception, tokens, HTML, or transported body. A failed sink is
-- recorded independently of the already committed source media operation.
CREATE TABLE "NotificationDeliveryAttempts" (
    "Id" bigint GENERATED BY DEFAULT AS IDENTITY,
    "NotificationDeliveryId" bigint NOT NULL,
    "AttemptNumber" integer NOT NULL,
    "Succeeded" boolean NOT NULL,
    "DurationMs" bigint,
    "ErrorCode" varchar(120),
    "AttemptedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_NotificationDeliveryAttempts" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_NotificationDeliveryAttempts_NotificationDeliveries" FOREIGN KEY ("NotificationDeliveryId")
        REFERENCES "NotificationDeliveries" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "UX_NotificationDeliveryAttempts_Delivery_Number" UNIQUE ("NotificationDeliveryId","AttemptNumber"),
    CONSTRAINT "CK_NotificationDeliveryAttempts_Attempt" CHECK ("AttemptNumber" > 0),
    CONSTRAINT "CK_NotificationDeliveryAttempts_Duration" CHECK ("DurationMs" IS NULL OR "DurationMs" >= 0),
    CONSTRAINT "CK_NotificationDeliveryAttempts_Result"
        CHECK (("Succeeded" AND "ErrorCode" IS NULL)
            OR (NOT "Succeeded" AND "ErrorCode" IS NOT NULL AND length(btrim("ErrorCode")) > 0))
);

CREATE INDEX "IX_Events_CreatedAt_Id" ON "Events" ("CreatedAt" DESC,"Id" DESC);
CREATE INDEX "IX_Events_Profile_CreatedAt" ON "Events" ("ProfileId","CreatedAt" DESC,"Id" DESC)
    WHERE "ProfileId" IS NOT NULL;
CREATE INDEX "IX_Notifications_Profile_CreatedAt" ON "Notifications" ("ProfileId","CreatedAt" DESC,"Id" DESC)
    WHERE "ProfileId" IS NOT NULL;
CREATE INDEX "IX_Notifications_Account_CreatedAt" ON "Notifications" ("AccountId","CreatedAt" DESC,"Id" DESC)
    WHERE "AccountId" IS NOT NULL;
CREATE INDEX "IX_NotificationDeliveries_CreatedAt" ON "NotificationDeliveries" ("CreatedAt" DESC,"Id" DESC);
CREATE INDEX "IX_NotificationDeliveryAttempts_Delivery_Time" ON "NotificationDeliveryAttempts"
    ("NotificationDeliveryId","AttemptedAt" DESC,"Id" DESC);
COMMIT;
