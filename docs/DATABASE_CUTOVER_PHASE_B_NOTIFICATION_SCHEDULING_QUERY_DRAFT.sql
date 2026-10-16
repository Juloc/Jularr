-- Internal Service READ for one trusted delivery occurrence; no external transport I/O.
WITH "DeliveryClock" AS MATERIALIZED (
    SELECT
        delivery."Id",
        delivery."NotificationChannelTypeId",
        profile."TimeZone",
        schedule."QuietHoursEnabled",
        schedule."QuietHoursStart",
        schedule."QuietHoursEnd",
        COALESCE(category."BypassesQuietHours", FALSE) AS "BypassesQuietHours",
        (@Now AT TIME ZONE profile."TimeZone")::date AS "LocalDate",
        (@Now AT TIME ZONE profile."TimeZone")::time AS "LocalTime"
    FROM
        "NotificationDeliveries" AS delivery
    LEFT JOIN
        "Profiles" AS profile ON profile."Id" = delivery."RecipientProfileId"
    LEFT JOIN
        "NotificationSchedules" AS schedule ON schedule."ProfileId" = profile."Id"
    LEFT JOIN
        "Events" AS event ON event."Id" = delivery."EventId"
    LEFT JOIN
        "NotificationEventCategoryTypes" AS category ON category."Id" = event."NotificationEventCategoryTypeId"
    WHERE
        delivery."Id" = @NotificationDeliveryId
)
SELECT
    clock."Id",
    CASE
        WHEN clock."NotificationChannelTypeId" = 1 OR clock."BypassesQuietHours" OR clock."QuietHoursEnabled" IS DISTINCT FROM TRUE THEN @Now
        WHEN clock."QuietHoursStart" < clock."QuietHoursEnd"
            AND clock."LocalTime" >= clock."QuietHoursStart" AND clock."LocalTime" < clock."QuietHoursEnd"
            THEN (clock."LocalDate" + clock."QuietHoursEnd") AT TIME ZONE clock."TimeZone"
        WHEN clock."QuietHoursStart" > clock."QuietHoursEnd" AND clock."LocalTime" >= clock."QuietHoursStart"
            THEN (clock."LocalDate" + 1 + clock."QuietHoursEnd") AT TIME ZONE clock."TimeZone"
        WHEN clock."QuietHoursStart" > clock."QuietHoursEnd" AND clock."LocalTime" < clock."QuietHoursEnd"
            THEN (clock."LocalDate" + clock."QuietHoursEnd") AT TIME ZONE clock."TimeZone"
        ELSE @Now
    END AS "NextAllowedAt"
FROM
    "DeliveryClock" AS clock;
