BEGIN;

CREATE TABLE "AiProviderTypes" (
    "Id" smallint NOT NULL,
    "Key" text NOT NULL,
    CONSTRAINT "PK_AiProviderTypes" PRIMARY KEY ("Id"),
    CONSTRAINT "UX_AiProviderTypes_Key" UNIQUE ("Key"),
    CONSTRAINT "CK_AiProviderTypes_Id" CHECK ("Id" BETWEEN 0 AND 255),
    CONSTRAINT "CK_AiProviderTypes_Key" CHECK (length(btrim("Key")) > 0)
);
INSERT INTO "AiProviderTypes" ("Id", "Key")
VALUES
    (1, 'server'),
    (2, 'open_ai_compatible');

CREATE TABLE "AiOperationTypes" (
    "Id" smallint NOT NULL,
    "Key" text NOT NULL,
    CONSTRAINT "PK_AiOperationTypes" PRIMARY KEY ("Id"),
    CONSTRAINT "UX_AiOperationTypes_Key" UNIQUE ("Key"),
    CONSTRAINT "CK_AiOperationTypes_Id" CHECK ("Id" BETWEEN 0 AND 255),
    CONSTRAINT "CK_AiOperationTypes_Key" CHECK (length(btrim("Key")) > 0)
);
INSERT INTO "AiOperationTypes" ("Id", "Key")
VALUES
    (1, 'sentence_explanation'),
    (2, 'novel_translation'),
    (3, 'book_translation'),
    (4, 'book_analysis'),
    (5, 'book_edit'),
    (6, 'book_qa'),
    (7, 'book_memory'),
    (8, 'novel_mapping'),
    (9, 'story_context'),
    (10, 'ui_translation'),
    (11, 'chapter_artwork');

CREATE TABLE "AiTranslationModeTypes" (
    "Id" smallint NOT NULL,
    "Key" text NOT NULL,
    CONSTRAINT "PK_AiTranslationModeTypes" PRIMARY KEY ("Id"),
    CONSTRAINT "UX_AiTranslationModeTypes_Key" UNIQUE ("Key"),
    CONSTRAINT "CK_AiTranslationModeTypes_Id" CHECK ("Id" BETWEEN 0 AND 255),
    CONSTRAINT "CK_AiTranslationModeTypes_Key" CHECK (length(btrim("Key")) > 0)
);
INSERT INTO "AiTranslationModeTypes" ("Id", "Key")
VALUES
    (1, 'efficient'),
    (2, 'quality'),
    (3, 'maximum');

CREATE TABLE "AiUsageKindTypes" (
    "Id" smallint NOT NULL,
    "Key" text NOT NULL,
    CONSTRAINT "PK_AiUsageKindTypes" PRIMARY KEY ("Id"),
    CONSTRAINT "UX_AiUsageKindTypes_Key" UNIQUE ("Key"),
    CONSTRAINT "CK_AiUsageKindTypes_Id" CHECK ("Id" BETWEEN 0 AND 255),
    CONSTRAINT "CK_AiUsageKindTypes_Key" CHECK (length(btrim("Key")) > 0)
);
INSERT INTO "AiUsageKindTypes" ("Id", "Key")
VALUES
    (1, 'request'),
    (2, 'cache_hit'),
    (3, 'resumed_chunk');

CREATE TABLE "AiInvocationPhaseTypes" (
    "Id" smallint NOT NULL,
    "Key" text NOT NULL,
    CONSTRAINT "PK_AiInvocationPhaseTypes" PRIMARY KEY ("Id"),
    CONSTRAINT "UX_AiInvocationPhaseTypes_Key" UNIQUE ("Key"),
    CONSTRAINT "CK_AiInvocationPhaseTypes_Id" CHECK ("Id" BETWEEN 0 AND 255),
    CONSTRAINT "CK_AiInvocationPhaseTypes_Key" CHECK (length(btrim("Key")) > 0)
);
INSERT INTO "AiInvocationPhaseTypes" ("Id", "Key")
VALUES
    (1, 'preparing_context'),
    (2, 'provider_call');

CREATE TABLE "ProfileAiSettings" (
    "ProfileId" bigint NOT NULL,
    "AiProviderTypeId" smallint NOT NULL DEFAULT 1,
    "AiTranslationModeTypeId" smallint NOT NULL DEFAULT 1,
    "BaseUrl" text,
    "CredentialStorageKey" varchar(200),
    "Model" varchar(120),
    "ImageModel" varchar(120),
    "ReasoningEffort" varchar(32),
    "ServiceTier" varchar(32),
    "Verbosity" varchar(32),
    "MaxOutputTokens" integer,
    "ContextBudgetTokens" integer,
    "MaxRetries" integer,
    "TimeoutSeconds" integer,
    "DailyTokenBudget" integer,
    "SessionTokenBudget" integer,
    "BudgetWarningPercent" smallint,
    "MaxConcurrentJobs" integer,
    "FallbackModelEnabled" boolean NOT NULL DEFAULT FALSE,
    "Revision" bigint NOT NULL DEFAULT 0,
    "UpdatedAt" timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT "PK_ProfileAiSettings" PRIMARY KEY ("ProfileId"),
    CONSTRAINT "FK_ProfileAiSettings_Profiles" FOREIGN KEY ("ProfileId") REFERENCES "Profiles" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_ProfileAiSettings_Providers" FOREIGN KEY ("AiProviderTypeId") REFERENCES "AiProviderTypes" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_ProfileAiSettings_TranslationModes" FOREIGN KEY ("AiTranslationModeTypeId") REFERENCES "AiTranslationModeTypes" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "CK_ProfileAiSettings_Connection" CHECK (
        ("AiProviderTypeId" = 1 AND "BaseUrl" IS NULL AND "CredentialStorageKey" IS NULL)
        OR ("AiProviderTypeId" = 2 AND "BaseUrl" IS NOT NULL AND "CredentialStorageKey" IS NOT NULL
            AND "BaseUrl" ~ '^https?://[^/@?#[:space:]]+(/[^?#[:space:]]*)?$'
            AND "CredentialStorageKey" ~ '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,199}$')),
    CONSTRAINT "CK_ProfileAiSettings_Model" CHECK (
        ("Model" IS NULL OR "Model" ~ '^[A-Za-z0-9][^[:space:][:cntrl:]]{0,119}$')
        AND ("ImageModel" IS NULL OR length(btrim("ImageModel")) > 0)),
    CONSTRAINT "CK_ProfileAiSettings_Options" CHECK (
        ("ReasoningEffort" IS NULL OR "ReasoningEffort" ~ '^[a-z0-9][a-z0-9_-]{0,31}$')
        AND ("ServiceTier" IS NULL OR "ServiceTier" ~ '^[a-z0-9][a-z0-9_-]{0,31}$')
        AND ("Verbosity" IS NULL OR "Verbosity" ~ '^[a-z0-9][a-z0-9_-]{0,31}$')
        AND ("AiProviderTypeId" <> 2 OR "Verbosity" IS NULL)),
    CONSTRAINT "CK_ProfileAiSettings_Limits" CHECK (
        ("MaxOutputTokens" IS NULL OR "MaxOutputTokens" BETWEEN 1 AND 128000)
        AND ("ContextBudgetTokens" IS NULL OR "ContextBudgetTokens" BETWEEN 1 AND 200000)
        AND ("MaxRetries" IS NULL OR "MaxRetries" BETWEEN 0 AND 5)
        AND ("TimeoutSeconds" IS NULL OR "TimeoutSeconds" BETWEEN 5 AND 900)
        AND ("DailyTokenBudget" IS NULL OR "DailyTokenBudget" BETWEEN 1 AND 1000000000)
        AND ("SessionTokenBudget" IS NULL OR "SessionTokenBudget" BETWEEN 1 AND 1000000000)
        AND ("BudgetWarningPercent" IS NULL OR "BudgetWarningPercent" BETWEEN 1 AND 100)
        AND ("MaxConcurrentJobs" IS NULL OR "MaxConcurrentJobs" BETWEEN 1 AND 8)),
    CONSTRAINT "CK_ProfileAiSettings_Revision" CHECK ("Revision" >= 0)
);

CREATE TABLE "ProfileAiOperationSettings" (
    "ProfileId" bigint NOT NULL,
    "AiOperationTypeId" smallint NOT NULL,
    "Model" varchar(120),
    "ReasoningEffort" varchar(32),
    "Verbosity" varchar(32),
    "MaxOutputTokens" integer,
    "ContextBudgetTokens" integer,
    "MaxRetries" integer,
    "TimeoutSeconds" integer,
    CONSTRAINT "PK_ProfileAiOperationSettings" PRIMARY KEY ("ProfileId", "AiOperationTypeId"),
    CONSTRAINT "FK_ProfileAiOperationSettings_ProfileSettings" FOREIGN KEY ("ProfileId") REFERENCES "ProfileAiSettings" ("ProfileId") ON DELETE RESTRICT,
    CONSTRAINT "FK_ProfileAiOperationSettings_Operations" FOREIGN KEY ("AiOperationTypeId") REFERENCES "AiOperationTypes" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "CK_ProfileAiOperationSettings_Override" CHECK (
        "AiOperationTypeId" <> 11
        AND num_nonnulls("Model", "ReasoningEffort", "Verbosity", "MaxOutputTokens", "ContextBudgetTokens", "MaxRetries", "TimeoutSeconds") > 0),
    CONSTRAINT "CK_ProfileAiOperationSettings_Options" CHECK (
        ("Model" IS NULL OR "Model" ~ '^[A-Za-z0-9][^[:space:][:cntrl:]]{0,119}$')
        AND ("ReasoningEffort" IS NULL OR "ReasoningEffort" ~ '^[a-z0-9][a-z0-9_-]{0,31}$')
        AND ("Verbosity" IS NULL OR "Verbosity" ~ '^[a-z0-9][a-z0-9_-]{0,31}$')
        AND ("MaxOutputTokens" IS NULL OR "MaxOutputTokens" BETWEEN 1 AND 128000)
        AND ("ContextBudgetTokens" IS NULL OR "ContextBudgetTokens" BETWEEN 1 AND 200000)
        AND ("MaxRetries" IS NULL OR "MaxRetries" BETWEEN 0 AND 5)
        AND ("TimeoutSeconds" IS NULL OR "TimeoutSeconds" BETWEEN 5 AND 900))
);

CREATE TABLE "AiInvocations" (
    "OperationId" bigint NOT NULL,
    "ProfileId" bigint NOT NULL,
    "AiOperationTypeId" smallint NOT NULL,
    "AiProviderTypeId" smallint,
    "AiInvocationPhaseTypeId" smallint,
    "Model" varchar(120),
    "Transport" varchar(100),
    "ReasoningEffort" varchar(32),
    "ServiceTier" varchar(32),
    "MaxOutputTokens" integer,
    "ContextWindow" bigint,
    "SourceTokens" bigint NOT NULL DEFAULT 0,
    "ContextTokens" bigint NOT NULL DEFAULT 0,
    "FullContextTokens" bigint NOT NULL DEFAULT 0,
    "ProgressCurrent" integer,
    "ProgressTotal" integer,
    CONSTRAINT "PK_AiInvocations" PRIMARY KEY ("OperationId"),
    CONSTRAINT "UX_AiInvocations_Scope" UNIQUE ("OperationId", "ProfileId", "AiOperationTypeId"),
    CONSTRAINT "FK_AiInvocations_Operations" FOREIGN KEY ("OperationId") REFERENCES "Operations" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_AiInvocations_Profiles" FOREIGN KEY ("ProfileId") REFERENCES "Profiles" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_AiInvocations_OperationTypes" FOREIGN KEY ("AiOperationTypeId") REFERENCES "AiOperationTypes" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_AiInvocations_Providers" FOREIGN KEY ("AiProviderTypeId") REFERENCES "AiProviderTypes" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_AiInvocations_Phases" FOREIGN KEY ("AiInvocationPhaseTypeId") REFERENCES "AiInvocationPhaseTypes" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "CK_AiInvocations_Context" CHECK (
        "SourceTokens" >= 0 AND "ContextTokens" >= 0 AND "FullContextTokens" >= "ContextTokens"
        AND ("ContextWindow" IS NULL OR "ContextWindow" > 0)
        AND ("MaxOutputTokens" IS NULL OR "MaxOutputTokens" BETWEEN 1 AND 128000)),
    CONSTRAINT "CK_AiInvocations_Progress" CHECK (
        ("ProgressCurrent" IS NULL AND "ProgressTotal" IS NULL)
        OR ("ProgressCurrent" IS NOT NULL AND "ProgressTotal" IS NOT NULL
            AND "ProgressTotal" > 0 AND "ProgressCurrent" BETWEEN 1 AND "ProgressTotal")),
    CONSTRAINT "CK_AiInvocations_Options" CHECK (
        ("Model" IS NULL OR "Model" ~ '^[A-Za-z0-9][^[:space:][:cntrl:]]{0,119}$')
        AND ("Transport" IS NULL OR length(btrim("Transport")) > 0)
        AND ("ReasoningEffort" IS NULL OR "ReasoningEffort" ~ '^[a-z0-9][a-z0-9_-]{0,31}$')
        AND ("ServiceTier" IS NULL OR "ServiceTier" ~ '^[a-z0-9][a-z0-9_-]{0,31}$'))
);
CREATE INDEX "IX_AiInvocations_Profile" ON "AiInvocations" ("ProfileId", "OperationId");

CREATE TABLE "AiUsageMeasurements" (
    "Id" bigint GENERATED BY DEFAULT AS IDENTITY,
    "ProfileId" bigint NOT NULL,
    "OperationId" bigint NOT NULL,
    "AiOperationTypeId" smallint NOT NULL,
    "AiProviderTypeId" smallint,
    "AiUsageKindTypeId" smallint NOT NULL,
    "OperationStatusTypeId" smallint NOT NULL,
    "Model" varchar(120),
    "RecordedAt" timestamptz NOT NULL,
    "Day" date GENERATED ALWAYS AS (("RecordedAt" AT TIME ZONE 'UTC')::date) STORED,
    "IsEstimated" boolean NOT NULL,
    "InputTokens" bigint NOT NULL,
    "CachedInputTokens" bigint NOT NULL,
    "OutputTokens" bigint NOT NULL,
    "ReasoningOutputTokens" bigint NOT NULL,
    "ContextTokens" bigint NOT NULL,
    "FullContextTokens" bigint NOT NULL,
    "DurationMs" bigint NOT NULL,
    "Retries" integer NOT NULL,
    CONSTRAINT "PK_AiUsageMeasurements" PRIMARY KEY ("Id"),
    CONSTRAINT "UX_AiUsageMeasurements_Invocation" UNIQUE ("OperationId"),
    CONSTRAINT "FK_AiUsageMeasurements_Invocation" FOREIGN KEY ("OperationId", "ProfileId", "AiOperationTypeId")
        REFERENCES "AiInvocations" ("OperationId", "ProfileId", "AiOperationTypeId") ON DELETE RESTRICT,
    CONSTRAINT "FK_AiUsageMeasurements_Providers" FOREIGN KEY ("AiProviderTypeId") REFERENCES "AiProviderTypes" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_AiUsageMeasurements_Kinds" FOREIGN KEY ("AiUsageKindTypeId") REFERENCES "AiUsageKindTypes" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_AiUsageMeasurements_Outcomes" FOREIGN KEY ("OperationStatusTypeId") REFERENCES "OperationStatusTypes" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "CK_AiUsageMeasurements_Outcome" CHECK ("OperationStatusTypeId" IN (3, 4, 5)),
    CONSTRAINT "CK_AiUsageMeasurements_Counts" CHECK (
        "InputTokens" >= 0 AND "CachedInputTokens" BETWEEN 0 AND "InputTokens"
        AND "OutputTokens" >= 0 AND "ReasoningOutputTokens" BETWEEN 0 AND "OutputTokens"
        AND "ContextTokens" >= 0 AND "FullContextTokens" >= "ContextTokens"
        AND "DurationMs" >= 0 AND "Retries" >= 0
        AND (NOT "IsEstimated" OR ("CachedInputTokens" = 0 AND "ReasoningOutputTokens" = 0))),
    CONSTRAINT "CK_AiUsageMeasurements_Kind" CHECK (
        ("AiUsageKindTypeId" = 1 AND "AiProviderTypeId" IS NOT NULL)
        OR ("AiUsageKindTypeId" IN (2, 3) AND "OperationStatusTypeId" = 3
            AND "IsEstimated" = FALSE AND "InputTokens" = 0 AND "CachedInputTokens" = 0
            AND "OutputTokens" = 0 AND "ReasoningOutputTokens" = 0 AND "ContextTokens" = 0
            AND "FullContextTokens" = 0 AND "DurationMs" = 0 AND "Retries" = 0)),
    CONSTRAINT "CK_AiUsageMeasurements_Model" CHECK (
        "Model" IS NULL OR "Model" ~ '^[A-Za-z0-9][^[:space:][:cntrl:]]{0,119}$')
);
CREATE INDEX "IX_AiUsageMeasurements_Profile_Day" ON "AiUsageMeasurements" ("ProfileId", "Day");
CREATE INDEX "IX_AiUsageMeasurements_Day" ON "AiUsageMeasurements" ("Day");

CREATE FUNCTION "RejectAiUsageMutation"() RETURNS trigger
LANGUAGE plpgsql AS $immutable_usage$
BEGIN
    IF NEW IS DISTINCT FROM OLD THEN
        RAISE EXCEPTION USING ERRCODE = '23514', CONSTRAINT = 'CK_AiUsageMeasurements_Immutable',
            MESSAGE = 'A recorded AI measurement is immutable';
    END IF;
    RETURN OLD;
END $immutable_usage$;
CREATE TRIGGER "TR_AiUsageMeasurements_Immutable" BEFORE UPDATE ON "AiUsageMeasurements"
    FOR EACH ROW EXECUTE FUNCTION "RejectAiUsageMutation"();

COMMIT;
