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
    AND provider."IsMediaConnectionEnabled" = TRUE;
