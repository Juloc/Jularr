-- Service READ: actor/profile come from the authenticated context, never a client role.
WITH "PageRoots" AS MATERIALIZED (
    SELECT
        inbox."Id",
        inbox."LastOccurredAt"
    FROM
        "Notifications" AS inbox
    INNER JOIN
        "Accounts" AS actor ON actor."Id" = @ActorAccountId AND actor."IsEnabled" = TRUE
    LEFT JOIN
        "AccountProfiles" AS membership
        ON membership."AccountId" = actor."Id" AND membership."ProfileId" = inbox."ProfileId"
    WHERE
        inbox."DismissedAt" IS NULL
        AND (@UnreadOnly = FALSE OR inbox."ReadAt" IS NULL)
        AND @PageSize BETWEEN 1 AND 100
        AND @Offset BETWEEN 0 AND 100000
        AND (
            (@ActiveProfileId IS NOT NULL AND inbox."ProfileId" = @ActiveProfileId AND membership."AccountId" IS NOT NULL)
            OR (@ActiveProfileId IS NULL AND inbox."AccountId" = actor."Id" AND actor."AccountRoleTypeId" IN (1, 3))
        )
    ORDER BY
        inbox."LastOccurredAt" DESC,
        inbox."Id" DESC
    LIMIT @PageSize
    OFFSET @Offset
)
SELECT
    inbox."PublicId" AS "NotificationId",
    event."Id" AS "EventId",
    event."NotificationEventCategoryTypeId",
    event."EventSeverityTypeId",
    event."MessageKey",
    event."MessageParams",
    work."PublicId" AS "WorkId",
    inbox."CreatedAt",
    inbox."LastOccurredAt",
    inbox."ReadAt",
    inbox."OccurrenceCount"
FROM
    "PageRoots" AS page_root
INNER JOIN
    "Notifications" AS inbox ON inbox."Id" = page_root."Id"
INNER JOIN
    "Events" AS event ON event."Id" = inbox."EventId"
LEFT JOIN
    "Works" AS work ON work."Id" = event."WorkId"
ORDER BY
    page_root."LastOccurredAt" DESC,
    page_root."Id" DESC;

-- Logic ENSURE: trusted recipient resolution; both writes run in one short transaction.
INSERT INTO "Notifications" AS inbox (
    "EventId",
    "ProfileId",
    "NotificationGroupKey",
    "CreatedAt",
    "LastOccurredAt",
    "OccurrenceCount"
)
SELECT
    event."Id",
    event."ProfileId",
    event."NotificationGroupKey",
    event."CreatedAt",
    event."CreatedAt",
    0
FROM
    "Events" AS event
WHERE
    event."Id" = @EventId
    AND event."ProfileId" = @RecipientProfileId
ON CONFLICT ON CONSTRAINT "UX_Notifications_Group_Recipient"
DO UPDATE SET
    "NotificationGroupKey" = inbox."NotificationGroupKey"
RETURNING
    inbox."Id";

-- Logic RECORD: the preceding upsert holds the inbox row lock until this transaction ends.
WITH recorded AS (
    INSERT INTO "NotificationEvents" (
        "NotificationId",
        "EventId",
        "ProfileId",
        "NotificationGroupKey"
    )
    SELECT
        inbox."Id",
        event."Id",
        event."ProfileId",
        event."NotificationGroupKey"
    FROM
        "Notifications" AS inbox
    INNER JOIN
        "Events" AS event ON event."NotificationGroupKey" = inbox."NotificationGroupKey"
    WHERE
        inbox."Id" = @NotificationId
        AND inbox."ProfileId" = @RecipientProfileId
        AND event."Id" = @EventId
        AND event."ProfileId" = inbox."ProfileId"
    ON CONFLICT ON CONSTRAINT "PK_NotificationEvents" DO NOTHING
    RETURNING
        "NotificationId",
        "EventId"
)
UPDATE
    "Notifications" AS inbox
SET
    "EventId" = CASE WHEN event."CreatedAt" >= inbox."LastOccurredAt" THEN event."Id" ELSE inbox."EventId" END,
    "LastOccurredAt" = GREATEST(inbox."LastOccurredAt", event."CreatedAt"),
    "OccurrenceCount" = inbox."OccurrenceCount" + 1,
    "ReadAt" = NULL,
    "DismissedAt" = NULL
FROM
    recorded AS occurrence
INNER JOIN
    "Events" AS event ON event."Id" = occurrence."EventId"
WHERE
    inbox."Id" = occurrence."NotificationId"
RETURNING
    inbox."Id",
    inbox."OccurrenceCount";
