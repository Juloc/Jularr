-- Internal Service READ immediately before transport I/O; availability is trusted registry state.
SELECT
    delivery."Id",
    delivery."OperationId",
    delivery."NotificationChannelTypeId",
    delivery."EventId",
    delivery."NotificationDigestBatchId",
    delivery."NotificationPushEndpointId",
    recipient."PublicId" AS "RecipientProfileId"
FROM
    "NotificationDeliveries" AS delivery
INNER JOIN
    "Operations" AS operation ON operation."Id" = delivery."OperationId"
INNER JOIN
    "Profiles" AS recipient ON recipient."Id" = delivery."RecipientProfileId"
INNER JOIN
    "Accounts" AS recipient_account
    ON recipient_account."Id" = COALESCE(delivery."AccountId", recipient."OwnerAccountId")
    AND recipient_account."IsEnabled" = TRUE
INNER JOIN
    "NotificationProfileChannels" AS profile_channel
    ON profile_channel."ProfileId" = recipient."Id"
    AND profile_channel."NotificationChannelTypeId" = delivery."NotificationChannelTypeId"
    AND profile_channel."IsEnabled" = TRUE
LEFT JOIN
    "NotificationSchedules" AS schedule ON schedule."ProfileId" = recipient."Id"
LEFT JOIN
    "Events" AS event ON event."Id" = delivery."EventId"
LEFT JOIN
    "NotificationDigestBatches" AS batch ON batch."Id" = delivery."NotificationDigestBatchId"
LEFT JOIN
    "NotificationPushEndpoints" AS endpoint ON endpoint."Id" = delivery."NotificationPushEndpointId"
LEFT JOIN
    "Accounts" AS endpoint_account ON endpoint_account."Id" = endpoint."AccountId"
LEFT JOIN
    "AccountProfiles" AS endpoint_access
    ON endpoint_access."AccountId" = endpoint."AccountId" AND endpoint_access."ProfileId" = endpoint."ProfileId"
LEFT JOIN
    "AccountSessions" AS endpoint_session ON endpoint_session."Id" = endpoint."AccountSessionId"
WHERE
    delivery."Id" = @NotificationDeliveryId
    AND delivery."NotificationChannelTypeId" = ANY(@AvailableChannels)
    AND delivery."DeliveredAt" IS NULL AND delivery."CancelledAt" IS NULL
    AND operation."OperationStatusTypeId" IN (1, 2)
    AND (operation."NextAttemptAt" IS NULL OR operation."NextAttemptAt" <= @Now)
    AND (delivery."NotificationDigestBatchId" IS NULL OR batch."ScheduledFor" <= @Now)
    AND (delivery."AccountId" IS NULL
         OR (recipient_account."AccountRoleTypeId" IN (1, 3) AND recipient."OwnerAccountId" = delivery."AccountId"))
    AND (
        (delivery."NotificationChannelTypeId" = 3 AND recipient_account."EmailVerifiedAt" IS NOT NULL)
        OR (delivery."NotificationChannelTypeId" = 2 AND endpoint."RevokedAt" IS NULL
            AND endpoint."TransportKey" = ANY(@AvailablePushTransports)
            AND endpoint_account."IsEnabled" = TRUE AND endpoint_access."AccountId" IS NOT NULL
            AND (endpoint."AccountSessionId" IS NULL OR (endpoint_session."RevokedAt" IS NULL AND endpoint_session."ExpiresAt" > @Now)))
    )
    AND (
        (delivery."EventId" IS NOT NULL AND EXISTS (
            SELECT 1
            FROM "NotificationSubscriptions" AS subscription
            INNER JOIN "NotificationSubscriptionChannels" AS selected_channel
                ON selected_channel."ProfileId" = subscription."ProfileId"
                AND selected_channel."NotificationEventCategoryTypeId" = subscription."NotificationEventCategoryTypeId"
            WHERE subscription."ProfileId" = recipient."Id"
                AND subscription."NotificationEventCategoryTypeId" = event."NotificationEventCategoryTypeId"
                AND subscription."IsEnabled" = TRUE AND subscription."NotificationTimingTypeId" = 1
                AND selected_channel."NotificationChannelTypeId" = delivery."NotificationChannelTypeId"
        ))
        OR (delivery."NotificationDigestBatchId" IS NOT NULL AND schedule."DigestEnabled" = TRUE
            AND EXISTS (
                SELECT 1 FROM "NotificationDigestChannels" AS digest_channel
                WHERE digest_channel."ProfileId" = recipient."Id"
                    AND digest_channel."NotificationChannelTypeId" = delivery."NotificationChannelTypeId"
            )
            AND EXISTS (
                SELECT 1 FROM "NotificationExternalOccurrences" AS item WHERE item."NotificationDigestBatchId" = delivery."NotificationDigestBatchId"
            )
            AND NOT EXISTS (
                SELECT 1
                FROM "NotificationExternalOccurrences" AS item
                WHERE item."NotificationDigestBatchId" = delivery."NotificationDigestBatchId" AND NOT EXISTS (
                    SELECT 1
                    FROM "NotificationSubscriptions" AS subscription
                    INNER JOIN "NotificationSubscriptionChannels" AS selected_channel
                        ON selected_channel."ProfileId" = subscription."ProfileId"
                        AND selected_channel."NotificationEventCategoryTypeId" = subscription."NotificationEventCategoryTypeId"
                    WHERE subscription."ProfileId" = recipient."Id" AND subscription."NotificationEventCategoryTypeId" = item."NotificationEventCategoryTypeId"
                        AND subscription."IsEnabled" = TRUE AND selected_channel."NotificationChannelTypeId" = delivery."NotificationChannelTypeId"
                )
            ))
    );
