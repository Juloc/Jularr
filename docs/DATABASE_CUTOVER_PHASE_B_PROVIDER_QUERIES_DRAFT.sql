-- Service FLOW_PERMISSION: fixed challenge purposes 7/8/9; availability alone never enables auth.
SELECT
    provider."Id" AS "ProviderInternalId",
    provider."Key" AS "ProviderKey"
FROM
    "Providers" AS provider
WHERE
    provider."Key" = @ProviderKey
    AND provider."IsEnabled" = TRUE
    AND (
        (@PurposeTypeId = 7 AND provider."IsAccountLoginEnabled" = TRUE)
        OR (@PurposeTypeId = 8 AND provider."IsAccountLinkEnabled" = TRUE)
        OR (@PurposeTypeId = 9 AND provider."IsMediaConnectionEnabled" = TRUE)
    );

-- Service PROFILE_MEDIA: current membership and opt-in before any protected credential resolution.
SELECT
    connection."Id" AS "ConnectionInternalId",
    provider."Key" AS "ProviderKey",
    connection."ExternalAccountId",
    connection."IsWatchlistSyncEnabled",
    connection."IsProgressSyncEnabled"
FROM
    "Accounts" AS account
INNER JOIN
    "AccountProfiles" AS membership
        ON membership."AccountId" = account."Id"
        AND membership."ProfileId" = @ActiveProfileId
INNER JOIN
    "ProviderMediaConnections" AS connection
        ON connection."ProfileId" = membership."ProfileId"
        AND connection."IsEnabled" = TRUE
INNER JOIN
    "Providers" AS provider
        ON provider."Id" = connection."ProviderId"
WHERE
    account."Id" = @ActorAccountId
    AND account."IsEnabled" = TRUE
    AND provider."Key" = @ProviderKey
    AND provider."IsEnabled" = TRUE
    AND provider."IsMediaConnectionEnabled" = TRUE
    AND connection."RevokedAt" IS NULL;

-- Service GRANTS: AdminSystem, metadata only; current Owner role before paging.
WITH "PageRoots" AS MATERIALIZED (
    SELECT
        server_grant."Id",
        server_grant."PublicId",
        server_grant."ProviderId",
        server_grant."DisplayName",
        server_grant."IsEnabled",
        server_grant."RevokedAt"
    FROM
        "Accounts" AS actor
    INNER JOIN
        "ProviderServerGrants" AS server_grant ON TRUE
    WHERE
        actor."Id" = @ActorAccountId
        AND actor."IsEnabled" = TRUE
        AND actor."AccountRoleTypeId" = 1
        AND @PageSize BETWEEN 1 AND 100
        AND @Offset BETWEEN 0 AND 100000
    ORDER BY
        server_grant."Id" DESC
    LIMIT @PageSize OFFSET @Offset
)
SELECT
    root."PublicId",
    provider."Key" AS "ProviderKey",
    root."DisplayName",
    root."IsEnabled",
    root."RevokedAt",
    sections."Count" AS "ApprovedSectionCount"
FROM
    "PageRoots" AS root
INNER JOIN
    "Providers" AS provider ON provider."Id" = root."ProviderId"
LEFT JOIN LATERAL (
    SELECT
        count(*) AS "Count"
    FROM
        "ProviderGrantLibrarySections" AS section
    WHERE
        section."ProviderServerGrantId" = root."Id"
        AND section."IsEnabled" = TRUE
) AS sections ON TRUE
ORDER BY
    root."Id" DESC;

-- Service CANDIDATES: internal candidate READ only, NOT a proof of remote user ACL.
WITH "Access" AS MATERIALIZED (
    SELECT
        access."ProfileId"
    FROM
        "Accounts" AS actor
    INNER JOIN
        "AccountProfiles" AS access ON access."AccountId" = actor."Id" AND access."ProfileId" = @ActiveProfileId
    WHERE
        actor."Id" = @ActorAccountId
        AND actor."IsEnabled" = TRUE
), "TargetWork" AS MATERIALIZED (
    SELECT
        work."Id"
    FROM
        "Works" AS work
    WHERE
        work."PublicId" = @WorkPublicId
), "WorkItems" AS MATERIALIZED (
    SELECT
        item."ProviderServerGrantId",
        item."ExternalItemKey",
        item."ExternalSectionId",
        item."Generation",
        item."WorkEpisodeId",
        item."VerifiedAt"
    FROM
        "TargetWork" AS work
    INNER JOIN
        "ProviderCatalogItems" AS item ON item."WorkId" = work."Id"
)
SELECT
    server_grant."Id" AS "ProviderServerGrantId",
    item."ExternalItemKey",
    item."WorkEpisodeId",
    connection."Id" AS "ProviderMediaConnectionId",
    connection."Revision" AS "ConnectionRevision",
    server_grant."Revision" AS "GrantRevision"
FROM
    "Access" AS access
INNER JOIN
    "ProviderMediaConnections" AS connection ON connection."ProfileId" = access."ProfileId"
INNER JOIN
    "Providers" AS provider ON provider."Id" = connection."ProviderId"
INNER JOIN
    "ProviderServerGrants" AS server_grant ON server_grant."ProviderId" = provider."Id"
INNER JOIN
    "ProviderGrantLibrarySections" AS section ON section."ProviderServerGrantId" = server_grant."Id" AND section."IsEnabled" = TRUE
INNER JOIN
    "ProviderCatalogCheckpoints" AS checkpoint ON checkpoint."ProviderServerGrantId" = section."ProviderServerGrantId"
        AND checkpoint."ExternalSectionId" = section."ExternalSectionId" AND checkpoint."GrantRevision" = server_grant."Revision"
INNER JOIN
    "WorkItems" AS item ON item."ProviderServerGrantId" = checkpoint."ProviderServerGrantId"
        AND item."ExternalSectionId" = checkpoint."ExternalSectionId" AND item."Generation" = checkpoint."Generation"
WHERE
    provider."IsEnabled" = TRUE
    AND provider."IsMediaConnectionEnabled" = TRUE
    AND connection."IsEnabled" = TRUE
    AND connection."RevokedAt" IS NULL
    AND server_grant."IsEnabled" = TRUE
    AND server_grant."RevokedAt" IS NULL
    AND @PageSize BETWEEN 1 AND 100
    AND @Offset BETWEEN 0 AND 100000
ORDER BY
    item."VerifiedAt" DESC,
    item."ProviderServerGrantId",
    item."ExternalItemKey"
LIMIT @PageSize OFFSET @Offset;

-- Logic ADVANCE: Logic holds grant/checkpoint lock, inserts verified page, then this CAS in SAME transaction.
UPDATE
    "ProviderCatalogCheckpoints" AS checkpoint
SET
    "NextStart" = @NextStart,
    "TotalSize" = @TotalSize,
    "LastPageStart" = @ExpectedStart,
    "PagesScanned" = checkpoint."PagesScanned" + 1,
    "UpdatedAt" = @Now,
    "CompletedAt" = CASE WHEN @NextStart IS NULL THEN @Now ELSE NULL END
FROM
    "ProviderServerGrants" AS server_grant,
    "ProviderGrantLibrarySections" AS section,
    "Providers" AS provider,
    "Accounts" AS actor,
    "Operations" AS operation
WHERE
    checkpoint."ProviderServerGrantId" = @ProviderServerGrantId
    AND checkpoint."ExternalSectionId" = @ExternalSectionId
    AND checkpoint."Generation" = @ExpectedGeneration
    AND checkpoint."GrantRevision" = @ExpectedGrantRevision
    AND checkpoint."NextStart" = @ExpectedStart
    AND (@NextStart IS NULL OR @NextStart > @ExpectedStart)
    AND @TotalSize >= 0
    AND server_grant."Id" = checkpoint."ProviderServerGrantId"
    AND server_grant."Revision" = checkpoint."GrantRevision"
    AND server_grant."IsEnabled" = TRUE
    AND server_grant."RevokedAt" IS NULL
    AND section."ProviderServerGrantId" = server_grant."Id"
    AND section."ExternalSectionId" = checkpoint."ExternalSectionId"
    AND section."IsEnabled" = TRUE
    AND provider."Id" = server_grant."ProviderId"
    AND provider."IsEnabled" = TRUE
    AND actor."Id" = checkpoint."RequestedByAccountId"
    AND actor."IsEnabled" = TRUE
    AND actor."AccountRoleTypeId" = 1
    AND operation."Id" = checkpoint."OperationId"
    AND operation."OperationStatusTypeId" = 2
    AND operation."ClaimedBy" = @TrustedWorkerKey
    AND operation."ClaimedUntil" > @Now
RETURNING
    checkpoint."Generation",
    checkpoint."PagesScanned";
