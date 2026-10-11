-- Service SETTINGS: never returns the credential reference or a claim that the adapter is connected.
SELECT
    settings."AiProviderTypeId",
    settings."AiTranslationModeTypeId",
    settings."BaseUrl",
    settings."Model",
    settings."ImageModel",
    settings."ReasoningEffort",
    settings."ServiceTier",
    settings."Verbosity",
    settings."MaxOutputTokens",
    settings."ContextBudgetTokens",
    settings."MaxRetries",
    settings."TimeoutSeconds",
    settings."DailyTokenBudget",
    settings."SessionTokenBudget",
    settings."BudgetWarningPercent",
    settings."MaxConcurrentJobs",
    settings."FallbackModelEnabled",
    settings."CredentialStorageKey" IS NOT NULL AS "HasCredentialReference",
    settings."Revision"
FROM
    "ProfileAiSettings" AS settings
WHERE
    settings."ProfileId" = @ActiveProfileId
    AND EXISTS (
            SELECT
                1
            FROM
                "Accounts" AS account
            INNER JOIN
                "AccountProfiles" AS membership ON membership."AccountId" = account."Id"
            WHERE
                account."Id" = @ActorAccountId
                AND account."IsEnabled" = TRUE
                AND membership."ProfileId" = @ActiveProfileId
        );

-- Service OPTIONS: one canonical settings row and a bounded exact operation override.
SELECT
    settings."AiProviderTypeId",
    settings."AiTranslationModeTypeId",
    COALESCE(override."Model", settings."Model") AS "Model",
    COALESCE(override."ReasoningEffort", settings."ReasoningEffort") AS "ReasoningEffort",
    settings."ServiceTier",
    CASE WHEN settings."AiProviderTypeId" = 1 THEN COALESCE(override."Verbosity", settings."Verbosity") END AS "Verbosity",
    COALESCE(override."MaxOutputTokens", settings."MaxOutputTokens") AS "MaxOutputTokens",
    COALESCE(override."ContextBudgetTokens", settings."ContextBudgetTokens") AS "ContextBudgetTokens",
    COALESCE(override."MaxRetries", settings."MaxRetries") AS "MaxRetries",
    COALESCE(override."TimeoutSeconds", settings."TimeoutSeconds") AS "TimeoutSeconds"
FROM
    "ProfileAiSettings" AS settings
INNER JOIN
    "AiOperationTypes" AS operation_type ON operation_type."Id" = @AiOperationTypeId
LEFT JOIN
    "ProfileAiOperationSettings" AS override ON override."ProfileId" = settings."ProfileId"
        AND override."AiOperationTypeId" = operation_type."Id"
INNER JOIN
    "AccountProfiles" AS membership ON membership."ProfileId" = settings."ProfileId"
INNER JOIN
    "Accounts" AS account ON account."Id" = membership."AccountId"
WHERE
    settings."ProfileId" = @ActiveProfileId
    AND account."Id" = @ActorAccountId
    AND account."IsEnabled" = TRUE
    AND (operation_type."Id" <> 10 OR (account."AccountRoleTypeId" = 1 AND settings."AiProviderTypeId" = 1));

-- Service USAGE_TOTALS: fixed one-row aggregate over an authorized at-most-30-day interval.
WITH "Scope" AS MATERIALIZED (
    SELECT
        account."Id"
    FROM
        "Accounts" AS account
    INNER JOIN
        "AccountProfiles" AS membership ON membership."AccountId" = account."Id"
    WHERE
        account."Id" = @ActorAccountId
        AND account."IsEnabled" = TRUE
        AND membership."ProfileId" = @ActiveProfileId
        AND @AllProfiles IS NOT NULL
        AND (NOT @AllProfiles OR account."AccountRoleTypeId" = 1)
    AND @ThroughDay - @FromDay BETWEEN 0 AND 29
)
SELECT
    totals."Requests",
    totals."EstimatedRequests",
    totals."InputTokens",
    totals."EstimatedInputTokens",
    totals."CachedInputTokens",
    totals."OutputTokens",
    totals."EstimatedOutputTokens",
    totals."ReasoningOutputTokens",
    totals."ContextTokens",
    totals."FullContextTokens",
    totals."CacheHits",
    totals."ResumedChunks",
    totals."Retries",
    totals."Failures",
    totals."Cancellations",
    totals."DurationMs"
FROM
    "Scope" AS scope
CROSS JOIN LATERAL (
    SELECT
        count(*) FILTER (WHERE measurement."AiUsageKindTypeId" = 1) AS "Requests",
        count(*) FILTER (WHERE measurement."AiUsageKindTypeId" = 1 AND measurement."IsEstimated") AS "EstimatedRequests",
        COALESCE(sum(measurement."InputTokens") FILTER (WHERE NOT measurement."IsEstimated"), 0)::bigint AS "InputTokens",
        COALESCE(sum(measurement."InputTokens") FILTER (WHERE measurement."IsEstimated"), 0)::bigint AS "EstimatedInputTokens",
        COALESCE(sum(measurement."CachedInputTokens"), 0)::bigint AS "CachedInputTokens",
        COALESCE(sum(measurement."OutputTokens") FILTER (WHERE NOT measurement."IsEstimated"), 0)::bigint AS "OutputTokens",
        COALESCE(sum(measurement."OutputTokens") FILTER (WHERE measurement."IsEstimated"), 0)::bigint AS "EstimatedOutputTokens",
        COALESCE(sum(measurement."ReasoningOutputTokens"), 0)::bigint AS "ReasoningOutputTokens",
        COALESCE(sum(measurement."ContextTokens"), 0)::bigint AS "ContextTokens",
        COALESCE(sum(measurement."FullContextTokens"), 0)::bigint AS "FullContextTokens",
        count(*) FILTER (WHERE measurement."AiUsageKindTypeId" = 2) AS "CacheHits",
        count(*) FILTER (WHERE measurement."AiUsageKindTypeId" = 3) AS "ResumedChunks",
        COALESCE(sum(measurement."Retries"), 0)::bigint AS "Retries",
        count(*) FILTER (WHERE measurement."OperationStatusTypeId" = 4) AS "Failures",
        count(*) FILTER (WHERE measurement."OperationStatusTypeId" = 5) AS "Cancellations",
        COALESCE(sum(measurement."DurationMs"), 0)::bigint AS "DurationMs"
    FROM
        "AiUsageMeasurements" AS measurement
    WHERE
        scope."Id" = @ActorAccountId
        AND (@AllProfiles OR measurement."ProfileId" = @ActiveProfileId)
        AND measurement."Day" BETWEEN @FromDay AND @ThroughDay
) AS totals;

-- Service USAGE_GROUPS: grouped roots are paged before resolving their operation names.
WITH "Scope" AS MATERIALIZED (
    SELECT
        account."Id"
    FROM
        "Accounts" AS account
    INNER JOIN
        "AccountProfiles" AS membership ON membership."AccountId" = account."Id"
    WHERE
        account."Id" = @ActorAccountId
        AND account."IsEnabled" = TRUE
        AND membership."ProfileId" = @ActiveProfileId
        AND @AllProfiles IS NOT NULL
        AND (NOT @AllProfiles OR account."AccountRoleTypeId" = 1)
    AND @ThroughDay - @FromDay BETWEEN 0 AND 29
    AND @PageSize BETWEEN 1 AND 100
    AND @Offset BETWEEN 0 AND 100000
), "PageRoots" AS MATERIALIZED (
    SELECT
        measurement."AiProviderTypeId",
        measurement."AiOperationTypeId",
        measurement."Model",
        sum(measurement."InputTokens" + measurement."OutputTokens")::bigint AS "Tokens",
        count(*) FILTER (WHERE measurement."AiUsageKindTypeId" = 1) AS "Requests"
    FROM
        "Scope" AS scope
    INNER JOIN
        "AiUsageMeasurements" AS measurement ON scope."Id" = @ActorAccountId
    WHERE
        (@AllProfiles OR measurement."ProfileId" = @ActiveProfileId)
        AND measurement."Day" BETWEEN @FromDay AND @ThroughDay
    GROUP BY
        measurement."AiProviderTypeId",
        measurement."AiOperationTypeId",
        measurement."Model"
    ORDER BY
        sum(measurement."InputTokens" + measurement."OutputTokens") DESC,
        measurement."AiProviderTypeId" ASC NULLS LAST,
        measurement."Model" ASC NULLS LAST,
        measurement."AiOperationTypeId" ASC
    LIMIT @PageSize OFFSET @Offset
)
SELECT
    root."AiProviderTypeId",
    operation_type."Key" AS "OperationKey",
    root."Model",
    root."Tokens",
    root."Requests"
FROM
    "PageRoots" AS root
INNER JOIN LATERAL (
    SELECT
        operation_type."Key"
    FROM
        "AiOperationTypes" AS operation_type
    WHERE
        operation_type."Id" = root."AiOperationTypeId"
    LIMIT 1
) AS operation_type ON TRUE
ORDER BY
    root."Tokens" DESC,
    root."AiProviderTypeId" ASC NULLS LAST,
    root."Model" ASC NULLS LAST,
    root."AiOperationTypeId" ASC;

-- Service ACTIVITY: Operations owns lifecycle, nullable measurement is unknown rather than zero.
WITH "Scope" AS MATERIALIZED (
    SELECT
        account."Id"
    FROM
        "Accounts" AS account
    INNER JOIN
        "AccountProfiles" AS membership ON membership."AccountId" = account."Id"
    WHERE
        account."Id" = @ActorAccountId
        AND account."IsEnabled" = TRUE
        AND membership."ProfileId" = @ActiveProfileId
        AND @AllProfiles IS NOT NULL
        AND (NOT @AllProfiles OR account."AccountRoleTypeId" = 1)
    AND @PageSize BETWEEN 1 AND 100
    AND @Offset BETWEEN 0 AND 100000
), "PageRoots" AS MATERIALIZED (
    SELECT
        invocation."OperationId",
        invocation."ProfileId",
        invocation."AiOperationTypeId",
        invocation."AiProviderTypeId",
        invocation."AiInvocationPhaseTypeId",
        invocation."Model",
        invocation."Transport",
        invocation."ReasoningEffort",
        invocation."ContextWindow",
        invocation."SourceTokens",
        invocation."ContextTokens",
        invocation."FullContextTokens",
        invocation."ProgressCurrent",
        invocation."ProgressTotal"
    FROM
        "Scope" AS scope
    INNER JOIN
        "AiInvocations" AS invocation ON scope."Id" = @ActorAccountId
    WHERE
        (@AllProfiles OR invocation."ProfileId" = @ActiveProfileId)
    ORDER BY
        invocation."OperationId" DESC
    LIMIT @PageSize OFFSET @Offset
)
SELECT
    operation."PublicId" AS "OperationPublicId",
    operation."OperationStatusTypeId",
    root."AiOperationTypeId",
    root."AiProviderTypeId",
    root."AiInvocationPhaseTypeId",
    root."Model",
    root."Transport",
    root."ReasoningEffort",
    root."ContextWindow",
    root."SourceTokens",
    root."ContextTokens",
    root."FullContextTokens",
    root."ProgressCurrent",
    root."ProgressTotal",
    operation."StartedAt",
    operation."FinishedAt",
    measurement."InputTokens",
    measurement."CachedInputTokens",
    measurement."OutputTokens",
    measurement."ReasoningOutputTokens",
    measurement."IsEstimated"
FROM
    "PageRoots" AS root
INNER JOIN LATERAL (
    SELECT
        operation."PublicId",
        operation."OperationStatusTypeId",
        operation."StartedAt",
        operation."FinishedAt"
    FROM
        "Operations" AS operation
    WHERE
        operation."Id" = root."OperationId"
    LIMIT 1
) AS operation ON TRUE
LEFT JOIN LATERAL (
    SELECT
        measurement."InputTokens",
        measurement."CachedInputTokens",
        measurement."OutputTokens",
        measurement."ReasoningOutputTokens",
        measurement."IsEstimated"
    FROM
        "AiUsageMeasurements" AS measurement
    WHERE
        measurement."OperationId" = root."OperationId"
    LIMIT 1
) AS measurement ON TRUE
ORDER BY
    root."OperationId" DESC;

-- Logic CREATE_SETTINGS: the configured default is not proof of provider availability.
WITH "Actor" AS MATERIALIZED (
    SELECT
        account."Id"
    FROM
        "Accounts" AS account
    WHERE
        account."Id" = @ActorAccountId
        AND account."IsEnabled" = TRUE
    FOR UPDATE OF account
), "Membership" AS MATERIALIZED (
    SELECT
        membership."ProfileId"
    FROM
        "Actor" AS actor
    INNER JOIN
        "AccountProfiles" AS membership ON membership."AccountId" = actor."Id"
    WHERE
        membership."ProfileId" = @ActiveProfileId
    FOR SHARE OF membership
)
INSERT INTO "ProfileAiSettings" AS settings ("ProfileId")
SELECT
    membership."ProfileId"
FROM
    "Membership" AS membership
ON CONFLICT ("ProfileId") DO NOTHING
RETURNING
    settings."Revision";

-- Logic SAVE_SETTINGS: profile membership and account stay locked through the short CAS transaction.
WITH "Actor" AS MATERIALIZED (
    SELECT
        account."Id"
    FROM
        "Accounts" AS account
    WHERE
        account."Id" = @ActorAccountId
        AND account."IsEnabled" = TRUE
    FOR UPDATE OF account
), "Membership" AS MATERIALIZED (
    SELECT
        membership."ProfileId"
    FROM
        "Actor" AS actor
    INNER JOIN
        "AccountProfiles" AS membership ON membership."AccountId" = actor."Id"
    WHERE
        membership."ProfileId" = @ActiveProfileId
    FOR SHARE OF membership
)
UPDATE
    "ProfileAiSettings" AS settings
SET
    "AiProviderTypeId" = @AiProviderTypeId,
    "AiTranslationModeTypeId" = @AiTranslationModeTypeId,
    "BaseUrl" = @BaseUrl,
    "Model" = @Model,
    "ImageModel" = @ImageModel,
    "ReasoningEffort" = @ReasoningEffort,
    "ServiceTier" = @ServiceTier,
    "Verbosity" = @Verbosity,
    "MaxOutputTokens" = @MaxOutputTokens,
    "ContextBudgetTokens" = @ContextBudgetTokens,
    "MaxRetries" = @MaxRetries,
    "TimeoutSeconds" = @TimeoutSeconds,
    "DailyTokenBudget" = @DailyTokenBudget,
    "SessionTokenBudget" = @SessionTokenBudget,
    "BudgetWarningPercent" = @BudgetWarningPercent,
    "MaxConcurrentJobs" = @MaxConcurrentJobs,
    "FallbackModelEnabled" = @FallbackModelEnabled,
    "CredentialStorageKey" = @CredentialStorageKey,
    "Revision" = settings."Revision" + 1,
    "UpdatedAt" = @Now
FROM
    "Membership" AS membership
WHERE
    settings."ProfileId" = membership."ProfileId"
    AND settings."Revision" = @ExpectedRevision
RETURNING
    settings."Revision";

-- Logic RECORD_USAGE: internal accepted invocation, never an externally writable accounting DTO.
INSERT INTO "AiUsageMeasurements" AS measurement (
    "ProfileId", "OperationId", "AiOperationTypeId", "AiProviderTypeId", "AiUsageKindTypeId",
    "OperationStatusTypeId", "Model", "RecordedAt", "IsEstimated", "InputTokens", "CachedInputTokens",
    "OutputTokens", "ReasoningOutputTokens", "ContextTokens", "FullContextTokens", "DurationMs", "Retries")
SELECT
    invocation."ProfileId",
    invocation."OperationId",
    invocation."AiOperationTypeId",
    invocation."AiProviderTypeId",
    @AiUsageKindTypeId,
    @OperationStatusTypeId,
    invocation."Model",
    @RecordedAt,
    @IsEstimated,
    @InputTokens,
    @CachedInputTokens,
    @OutputTokens,
    @ReasoningOutputTokens,
    @ContextTokens,
    @FullContextTokens,
    @DurationMs,
    @Retries
FROM
    "AiInvocations" AS invocation
WHERE
    invocation."OperationId" = @OperationId
ON CONFLICT ("OperationId") DO NOTHING
RETURNING
    measurement."Id";

-- Service USAGE_RECEIPT: internal exact retry acknowledgement, not a second additive daily write.
SELECT
    measurement."OperationId"
FROM
    "AiUsageMeasurements" AS measurement
WHERE
    measurement."OperationId" = @OperationId
    AND measurement."AiUsageKindTypeId" = @AiUsageKindTypeId
    AND measurement."OperationStatusTypeId" = @OperationStatusTypeId
    AND measurement."RecordedAt" = @RecordedAt
    AND measurement."IsEstimated" = @IsEstimated
    AND measurement."InputTokens" = @InputTokens
    AND measurement."CachedInputTokens" = @CachedInputTokens
    AND measurement."OutputTokens" = @OutputTokens
    AND measurement."ReasoningOutputTokens" = @ReasoningOutputTokens
    AND measurement."ContextTokens" = @ContextTokens
    AND measurement."FullContextTokens" = @FullContextTokens
    AND measurement."DurationMs" = @DurationMs
    AND measurement."Retries" = @Retries;

-- Logic SAVE_OVERRIDE: one parent revision serializes inherited settings and all operation overrides.
WITH "Actor" AS MATERIALIZED (
    SELECT
        account."Id"
    FROM
        "Accounts" AS account
    WHERE
        account."Id" = @ActorAccountId
        AND account."IsEnabled" = TRUE
        AND (@AiOperationTypeId <> 10 OR account."AccountRoleTypeId" = 1)
    FOR UPDATE OF account
), "Membership" AS MATERIALIZED (
    SELECT
        membership."ProfileId"
    FROM
        "Actor" AS actor
    INNER JOIN
        "AccountProfiles" AS membership ON membership."AccountId" = actor."Id"
    WHERE
        membership."ProfileId" = @ActiveProfileId
    FOR SHARE OF membership
), "Owner" AS (
    UPDATE
        "ProfileAiSettings" AS settings
    SET
        "Revision" = settings."Revision" + 1,
        "UpdatedAt" = @Now
    FROM
        "Membership" AS membership
    WHERE
        settings."ProfileId" = membership."ProfileId"
        AND settings."Revision" = @ExpectedRevision
        AND @AiOperationTypeId BETWEEN 1 AND 10
    RETURNING
        settings."ProfileId",
        settings."Revision"
), "Removed" AS (
    DELETE FROM
        "ProfileAiOperationSettings" AS override
    USING
        "Owner" AS owner
    WHERE
        override."ProfileId" = owner."ProfileId"
        AND override."AiOperationTypeId" = @AiOperationTypeId
        AND num_nonnulls(@Model, @ReasoningEffort, @Verbosity, @MaxOutputTokens, @ContextBudgetTokens, @MaxRetries, @TimeoutSeconds) = 0
    RETURNING
        override."ProfileId"
), "Saved" AS (
    INSERT INTO "ProfileAiOperationSettings" AS override (
        "ProfileId", "AiOperationTypeId", "Model", "ReasoningEffort", "Verbosity", "MaxOutputTokens", "ContextBudgetTokens", "MaxRetries", "TimeoutSeconds")
    SELECT
        owner."ProfileId",
        @AiOperationTypeId,
        @Model,
        @ReasoningEffort,
        @Verbosity,
        @MaxOutputTokens,
        @ContextBudgetTokens,
        @MaxRetries,
        @TimeoutSeconds
    FROM
        "Owner" AS owner
    WHERE
        num_nonnulls(@Model, @ReasoningEffort, @Verbosity, @MaxOutputTokens, @ContextBudgetTokens, @MaxRetries, @TimeoutSeconds) > 0
    ON CONFLICT ("ProfileId", "AiOperationTypeId") DO UPDATE SET
        "Model" = EXCLUDED."Model",
        "ReasoningEffort" = EXCLUDED."ReasoningEffort",
        "Verbosity" = EXCLUDED."Verbosity",
        "MaxOutputTokens" = EXCLUDED."MaxOutputTokens",
        "ContextBudgetTokens" = EXCLUDED."ContextBudgetTokens",
        "MaxRetries" = EXCLUDED."MaxRetries",
        "TimeoutSeconds" = EXCLUDED."TimeoutSeconds"
    RETURNING
        override."ProfileId"
)
SELECT
    owner."Revision"
FROM
    "Owner" AS owner;

-- Service OVERRIDES: bounded profile projection with one parent revision, no credential data.
SELECT
    override."AiOperationTypeId",
    override."Model",
    override."ReasoningEffort",
    override."Verbosity",
    override."MaxOutputTokens",
    override."ContextBudgetTokens",
    override."MaxRetries",
    override."TimeoutSeconds",
    settings."Revision"
FROM
    "ProfileAiSettings" AS settings
INNER JOIN
    "ProfileAiOperationSettings" AS override ON override."ProfileId" = settings."ProfileId"
INNER JOIN
    "AccountProfiles" AS membership ON membership."ProfileId" = settings."ProfileId"
INNER JOIN
    "Accounts" AS account ON account."Id" = membership."AccountId"
WHERE
    settings."ProfileId" = @ActiveProfileId
    AND account."Id" = @ActorAccountId
    AND account."IsEnabled" = TRUE
    AND (override."AiOperationTypeId" <> 10 OR account."AccountRoleTypeId" = 1)
    AND @PageSize BETWEEN 1 AND 100
    AND @Offset BETWEEN 0 AND 100000
ORDER BY
    override."AiOperationTypeId" ASC
LIMIT @PageSize OFFSET @Offset;
