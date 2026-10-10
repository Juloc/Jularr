-- Service SESSION: trusted SHA-256 token digest, never UUID-as-bearer.
SELECT
    session."Id",
    account."Id" AS "AccountId",
    account."PublicId" AS "AccountPublicId",
    account."AccountRoleTypeId",
    session."ActiveProfileId",
    profile."PublicId" AS "ProfilePublicId",
    session."RotationRevision"
FROM
    "AccountSessions" AS session
INNER JOIN
    "Accounts" AS account ON account."Id" = session."AccountId"
LEFT JOIN
    "AccountProfiles" AS access ON access."AccountId" = account."Id" AND access."ProfileId" = session."ActiveProfileId"
LEFT JOIN
    "Profiles" AS profile ON profile."Id" = access."ProfileId"
WHERE
    session."TokenHash" = @TokenHash
    AND session."RevokedAt" IS NULL
    AND session."ExpiresAt" > @Now
    AND session."CredentialRevision" = account."CredentialRevision"
    AND account."IsEnabled" = TRUE
    AND (session."ActiveProfileId" IS NULL OR access."ProfileId" IS NOT NULL);

-- Service SESSIONS: self-management metadata only, authorization before paging.
SELECT
    session."PublicId",
    session."CreatedAt",
    session."LastSeenAt",
    session."ExpiresAt",
    session."RevokedAt"
FROM
    "Accounts" AS actor
INNER JOIN
    "AccountSessions" AS session ON session."AccountId" = actor."Id"
WHERE
    actor."Id" = @ActorAccountId
    AND actor."IsEnabled" = TRUE
    AND @PageSize BETWEEN 1 AND 100
    AND @Offset BETWEEN 0 AND 100000
ORDER BY
    session."CreatedAt" DESC,
    session."Id" DESC
LIMIT @PageSize OFFSET @Offset;

-- Logic ROTATE: successful authentication, same row; old hash/revision is one-use CAS.
WITH "Actor" AS MATERIALIZED (
    SELECT
        account."Id",
        account."CredentialRevision"
    FROM
        "Accounts" AS account
    WHERE
        account."Id" = @ActorAccountId
        AND account."IsEnabled" = TRUE
    FOR UPDATE
)
UPDATE
    "AccountSessions" AS session
SET
    "TokenHash" = @NewTokenHash,
    "RotationRevision" = session."RotationRevision" + 1,
    "RotatedAt" = @Now,
    "ExpiresAt" = @NewExpiresAt
FROM
    "Actor" AS account
WHERE
    account."Id" = session."AccountId"
    AND session."AccountId" = @ActorAccountId
    AND session."TokenHash" = @OldTokenHash
    AND @NewTokenHash <> @OldTokenHash
    AND session."RotationRevision" = @ExpectedRotationRevision
    AND session."CredentialRevision" = account."CredentialRevision"
    AND session."RevokedAt" IS NULL
    AND session."ExpiresAt" > @Now
    AND @NewExpiresAt > @Now
RETURNING
    session."Id",
    session."RotationRevision";

-- Logic CHALLENGE: consume and apply the verified effect in one short transaction.
WITH "Actor" AS MATERIALIZED (
    SELECT
        account."Id",
        account."CredentialRevision"
    FROM
        "Accounts" AS account
    WHERE
        account."Id" = @ActorAccountId
        AND account."IsEnabled" = TRUE
    FOR UPDATE
)
UPDATE
    "AccountAuthChallenges" AS challenge
SET
    "ConsumedAt" = @Now
WHERE
    challenge."PublicId" = @ChallengePublicId
    AND challenge."TokenHash" = @BrowserTokenHash
    AND challenge."AuthChallengePurposeTypeId" = @PurposeTypeId
    AND challenge."AccountId" IS NOT DISTINCT FROM @ActorAccountId
    AND challenge."ConsumedAt" IS NULL
    AND challenge."ExpiresAt" > @Now
    AND (challenge."AccountId" IS NULL OR EXISTS (
        SELECT
            1
        FROM
            "Actor" AS account
        WHERE
            account."Id" = challenge."AccountId"
            AND account."CredentialRevision" = challenge."CredentialRevision"))
    AND (challenge."AuthChallengePurposeTypeId" NOT IN (7, 8, 9) OR EXISTS (
        SELECT
            1
        FROM
            "AccountExternalAuthFlows" AS flow
        INNER JOIN
            "Providers" AS provider ON provider."Id" = flow."ProviderId" AND provider."IsEnabled" = TRUE
        WHERE
            flow."AccountAuthChallengeId" = challenge."Id"
            AND flow."VerifiedExternalAccountId" IS NOT NULL
            AND flow."ProviderId" = @ProviderId
            AND flow."StartedProfileId" IS NOT DISTINCT FROM @ActiveProfileId
            AND (flow."StartedProfileId" IS NULL OR EXISTS (
                SELECT
                    1
                FROM
                    "AccountProfiles" AS access
                WHERE
                    access."AccountId" = challenge."AccountId"
                    AND access."ProfileId" = flow."StartedProfileId"))))
RETURNING
    challenge."Id";

-- Logic RECOVERY: verified high-entropy code hash; effect and consumption share transaction.
WITH "Actor" AS MATERIALIZED (
    SELECT
        account."Id"
    FROM
        "Accounts" AS account
    WHERE
        account."Id" = @ActorAccountId
        AND account."IsEnabled" = TRUE
    FOR UPDATE
)
UPDATE
    "AccountRecoveryCodes" AS code
SET
    "UsedAt" = @Now
FROM
    "Actor" AS account
WHERE
    account."Id" = code."AccountId"
    AND code."AccountId" = @ActorAccountId
    AND code."CodeHash" = @CodeHash
    AND code."UsedAt" IS NULL
RETURNING
    code."Id";

-- Logic TOTP: caller verifies RFC algorithm/window first; commit only a newer accepted step.
WITH "Actor" AS MATERIALIZED (
    SELECT
        account."Id"
    FROM
        "Accounts" AS account
    WHERE
        account."Id" = @ActorAccountId
        AND account."IsEnabled" = TRUE
    FOR UPDATE
)
UPDATE
    "AccountTotpFactors" AS factor
SET
    "LastAcceptedStep" = @VerifiedStep
FROM
    "Actor" AS account
WHERE
    account."Id" = factor."AccountId"
    AND factor."Id" = @FactorId
    AND factor."AccountId" = @ActorAccountId
    AND factor."ConfirmedAt" IS NOT NULL
    AND factor."RevokedAt" IS NULL
    AND @VerifiedStep > factor."LastAcceptedStep"
RETURNING
    factor."Id";

-- Logic INVALIDATE: verified credential change and this CAS are one transaction.
WITH "Actor" AS (
    UPDATE
        "Accounts" AS account
    SET
        "CredentialRevision" = account."CredentialRevision" + 1,
        "UpdatedAt" = @Now
    WHERE
        account."Id" = @ActorAccountId
        AND account."IsEnabled" = TRUE
        AND account."CredentialRevision" = @ExpectedCredentialRevision
    RETURNING
        account."Id",
        account."CredentialRevision"
), "RevokedSessions" AS (
    UPDATE
        "AccountSessions" AS session
    SET
        "RevokedAt" = @Now
    FROM
        "Actor" AS actor
    WHERE
        session."AccountId" = actor."Id"
        AND session."RevokedAt" IS NULL
    RETURNING
        session."Id"
), "ConsumedChallenges" AS (
    UPDATE
        "AccountAuthChallenges" AS challenge
    SET
        "ConsumedAt" = @Now
    FROM
        "Actor" AS actor
    WHERE
        challenge."AccountId" = actor."Id"
        AND challenge."ConsumedAt" IS NULL
    RETURNING
        challenge."Id"
)
SELECT
    actor."CredentialRevision"
FROM
    "Actor" AS actor;
