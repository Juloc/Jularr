-- Phase B: static, pre-authorized profile Reader annotation reads.
-- Only outward UUIDs and reader locators are projected, never StorageKey or internal IDs.
-- Service BOOKMARKS:
SELECT
    bookmark."PublicId" AS "ReaderBookmarkId",
    work."PublicId" AS "WorkId",
    edition."PublicId" AS "WorkEditionId",
    content."ContentRevision",
    bookmark."TextLocator",
    bookmark."CreatedAt"
FROM
    "Accounts" AS account
INNER JOIN
    "AccountProfiles" AS membership
        ON membership."AccountId" = account."Id"
        AND membership."ProfileId" = @ActiveProfileId
INNER JOIN
    "ReaderBookmarks" AS bookmark
        ON bookmark."ProfileId" = membership."ProfileId"
INNER JOIN
    "ReaderContent" AS content
        ON content."Id" = bookmark."ReaderContentId"
INNER JOIN
    "WorkEditions" AS edition
        ON edition."Id" = content."WorkEditionId"
        AND edition."WorkId" = content."WorkId"
INNER JOIN
    "Works" AS work
        ON work."Id" = edition."WorkId"
WHERE
    account."Id" = @ActorAccountId
    AND account."IsEnabled" = TRUE
    AND work."PublicId" = @WorkPublicId
    AND @PageSize BETWEEN 1 AND 100
    AND @Offset BETWEEN 0 AND 100000
ORDER BY
    bookmark."CreatedAt" DESC,
    bookmark."Id" DESC
LIMIT @PageSize
OFFSET @Offset;

-- Service HIGHLIGHTS:
SELECT
    highlight."PublicId" AS "ReaderHighlightId",
    work."PublicId" AS "WorkId",
    edition."PublicId" AS "WorkEditionId",
    content."ContentRevision",
    highlight."StartLocator",
    highlight."EndLocator",
    highlight."Note",
    highlight."CreatedAt"
FROM
    "Accounts" AS account
INNER JOIN
    "AccountProfiles" AS membership
        ON membership."AccountId" = account."Id"
        AND membership."ProfileId" = @ActiveProfileId
INNER JOIN
    "ReaderHighlights" AS highlight
        ON highlight."ProfileId" = membership."ProfileId"
INNER JOIN
    "ReaderContent" AS content
        ON content."Id" = highlight."ReaderContentId"
INNER JOIN
    "WorkEditions" AS edition
        ON edition."Id" = content."WorkEditionId"
        AND edition."WorkId" = content."WorkId"
INNER JOIN
    "Works" AS work
        ON work."Id" = edition."WorkId"
WHERE
    account."Id" = @ActorAccountId
    AND account."IsEnabled" = TRUE
    AND work."PublicId" = @WorkPublicId
    AND @PageSize BETWEEN 1 AND 100
    AND @Offset BETWEEN 0 AND 100000
ORDER BY
    highlight."CreatedAt" DESC,
    highlight."Id" DESC
LIMIT @PageSize
OFFSET @Offset;
