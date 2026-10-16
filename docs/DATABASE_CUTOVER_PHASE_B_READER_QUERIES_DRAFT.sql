-- Phase B: static, pre-authorized profile Reader annotation reads.
-- Only outward UUIDs and reader locators are projected, never StorageKey or internal IDs.
-- Service BOOKMARKS:
SELECT
    bookmark."PublicId" AS "ReaderBookmarkId",
    work."PublicId" AS "WorkId",
    edition."PublicId" AS "WorkEditionId",
    chapter."PublicId" AS "WorkChapterId",
    content."ContentRevision",
    content."VariantKey",
    bookmark."Locator",
    bookmark."Revision",
    bookmark."Label",
    bookmark."UpdatedAt",
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
LEFT JOIN
    "WorkChapters" AS chapter ON chapter."Id" = content."WorkChapterId"
WHERE
    account."Id" = @ActorAccountId
    AND account."IsEnabled" = TRUE
    AND work."PublicId" = @WorkPublicId
    AND content."PublishedAt" IS NOT NULL
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
    chapter."PublicId" AS "WorkChapterId",
    content."ContentRevision",
    content."VariantKey",
    highlight."Revision",
    highlight."StartLocator",
    highlight."EndLocator",
    highlight."SelectedText",
    highlight."Note",
    highlight."UpdatedAt",
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
LEFT JOIN
    "WorkChapters" AS chapter ON chapter."Id" = content."WorkChapterId"
WHERE
    account."Id" = @ActorAccountId
    AND account."IsEnabled" = TRUE
    AND work."PublicId" = @WorkPublicId
    AND content."PublishedAt" IS NOT NULL
    AND @PageSize BETWEEN 1 AND 100
    AND @Offset BETWEEN 0 AND 100000
ORDER BY
    highlight."CreatedAt" DESC,
    highlight."Id" DESC
LIMIT @PageSize
OFFSET @Offset;

-- Service PAGES:
WITH "Scope" AS MATERIALIZED (
    SELECT
        content."Id",
        work."PublicId" AS "WorkId",
        edition."PublicId" AS "WorkEditionId",
        chapter."PublicId" AS "WorkChapterId",
        content."ContentRevision",
        content."VariantKey"
    FROM
        "Accounts" AS account
    INNER JOIN
        "AccountProfiles" AS membership ON membership."AccountId" = account."Id" AND membership."ProfileId" = @ActiveProfileId
    INNER JOIN
        "WorkEditions" AS edition ON edition."PublicId" = @WorkEditionPublicId
    INNER JOIN
        "Works" AS work ON work."Id" = edition."WorkId"
    INNER JOIN
        "ReaderContent" AS content ON content."WorkEditionId" = edition."Id" AND content."WorkId" = edition."WorkId"
    LEFT JOIN
        "WorkChapters" AS chapter ON chapter."Id" = content."WorkChapterId"
    WHERE
        account."Id" = @ActorAccountId
        AND account."IsEnabled" = TRUE
        AND content."ContentRevision" = @ContentRevision
        AND chapter."PublicId" IS NOT DISTINCT FROM @ChapterPublicId
        AND content."VariantKey" = @VariantKey
        AND content."PublishedAt" IS NOT NULL
        AND @StartPageIndex >= 0
        AND @PageSize BETWEEN 1 AND 100
)
SELECT
    scope."WorkId",
    scope."WorkEditionId",
    scope."WorkChapterId",
    scope."ContentRevision",
    scope."VariantKey",
    page."PageIndex",
    page."SourcePageIndex",
    page."TextAnchor"
FROM
    "Scope" AS scope
INNER JOIN LATERAL (
    SELECT
        page."PageIndex",
        page."SourcePageIndex",
        page."TextAnchor"
    FROM
        "ReaderPages" AS page
    WHERE
        page."ReaderContentId" = scope."Id"
        AND page."PageIndex" >= @StartPageIndex
    ORDER BY
        page."PageIndex"
    LIMIT @PageSize
) AS page ON TRUE
ORDER BY
    page."PageIndex";

-- Service BOOKMARK:
SELECT
    bookmark."PublicId",
    bookmark."Revision",
    edition."PublicId" AS "EditionPublicId",
    content."VariantKey",
    chapter."PublicId" AS "WorkChapterId",
    content."ContentRevision",
    bookmark."Locator",
    bookmark."Label",
    bookmark."CreatedAt",
    bookmark."UpdatedAt"
FROM
    "ReaderBookmarks" AS bookmark
INNER JOIN
    "ReaderContent" AS content ON content."Id" = bookmark."ReaderContentId"
INNER JOIN
    "WorkEditions" AS edition ON edition."Id" = content."WorkEditionId"
INNER JOIN
    "AccountProfiles" AS membership ON membership."ProfileId" = bookmark."ProfileId"
INNER JOIN
    "Accounts" AS account ON account."Id" = membership."AccountId"
LEFT JOIN
    "WorkChapters" AS chapter ON chapter."Id" = content."WorkChapterId"
WHERE
    account."Id" = @ActorAccountId
    AND account."IsEnabled" = TRUE
    AND membership."ProfileId" = @ActiveProfileId
    AND bookmark."PublicId" = @PublicId
    AND content."PublishedAt" IS NOT NULL;

-- Service HIGHLIGHT:
SELECT
    highlight."PublicId",
    highlight."Revision",
    edition."PublicId" AS "EditionPublicId",
    content."VariantKey",
    chapter."PublicId" AS "WorkChapterId",
    content."ContentRevision",
    highlight."StartLocator",
    highlight."EndLocator",
    highlight."SelectedText",
    highlight."Note",
    highlight."CreatedAt",
    highlight."UpdatedAt"
FROM
    "ReaderHighlights" AS highlight
INNER JOIN
    "ReaderContent" AS content ON content."Id" = highlight."ReaderContentId"
INNER JOIN
    "WorkEditions" AS edition ON edition."Id" = content."WorkEditionId"
INNER JOIN
    "AccountProfiles" AS membership ON membership."ProfileId" = highlight."ProfileId"
INNER JOIN
    "Accounts" AS account ON account."Id" = membership."AccountId"
LEFT JOIN
    "WorkChapters" AS chapter ON chapter."Id" = content."WorkChapterId"
WHERE
    account."Id" = @ActorAccountId
    AND account."IsEnabled" = TRUE
    AND membership."ProfileId" = @ActiveProfileId
    AND highlight."PublicId" = @PublicId
    AND content."PublishedAt" IS NOT NULL;
