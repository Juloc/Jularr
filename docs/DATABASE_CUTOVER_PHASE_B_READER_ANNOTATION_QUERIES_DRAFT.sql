-- Logic BOOKMARK_CREATE: Service validates the exact immutable payload before entering this short transaction.
WITH "Actor" AS MATERIALIZED (
    SELECT
        account."Id"
    FROM
        "Accounts" AS account
    WHERE
        account."Id" = @ActorAccountId
        AND account."IsEnabled" = TRUE
    FOR UPDATE OF account
), "Scope" AS MATERIALIZED (
    SELECT
        membership."ProfileId"
    FROM
        "AccountProfiles" AS membership
    INNER JOIN
        "Actor" AS actor ON actor."Id" = membership."AccountId"
    WHERE
        membership."ProfileId" = @ActiveProfileId
    FOR SHARE OF membership
)
INSERT INTO
    "ReaderBookmarks" AS bookmark ("PublicId", "ProfileId", "ReaderContentId", "Locator", "Label", "CreatedAt", "UpdatedAt")
SELECT
    @PublicId,
    scope."ProfileId",
    content."Id",
    @Locator,
    @Label,
    @Now,
    @Now
FROM
    "Scope" AS scope
INNER JOIN
    "WorkEditions" AS edition ON edition."PublicId" = @EditionPublicId
INNER JOIN
    "ReaderContent" AS content ON content."WorkEditionId" = edition."Id"
LEFT JOIN
    "WorkChapters" AS chapter ON chapter."Id" = content."WorkChapterId"
WHERE
    chapter."PublicId" IS NOT DISTINCT FROM @ChapterPublicId
    AND content."VariantKey" = @VariantKey
    AND content."ContentRevision" = @ContentRevision
    AND content."PublishedAt" IS NOT NULL
ON CONFLICT ("PublicId") DO NOTHING
RETURNING
    bookmark."PublicId",
    bookmark."Revision";

-- Logic HIGHLIGHT_CREATE: SelectedText is the server-extracted UTF-16 selection, never a trusted client quote.
WITH "Actor" AS MATERIALIZED (
    SELECT
        account."Id"
    FROM
        "Accounts" AS account
    WHERE
        account."Id" = @ActorAccountId
        AND account."IsEnabled" = TRUE
    FOR UPDATE OF account
), "Scope" AS MATERIALIZED (
    SELECT
        membership."ProfileId"
    FROM
        "AccountProfiles" AS membership
    INNER JOIN
        "Actor" AS actor ON actor."Id" = membership."AccountId"
    WHERE
        membership."ProfileId" = @ActiveProfileId
    FOR SHARE OF membership
)
INSERT INTO
    "ReaderHighlights" AS highlight ("PublicId", "ProfileId", "ReaderContentId", "StartLocator", "EndLocator", "SelectedText", "Note", "CreatedAt", "UpdatedAt")
SELECT
    @PublicId,
    scope."ProfileId",
    content."Id",
    @StartLocator,
    @EndLocator,
    @SelectedText,
    @Note,
    @Now,
    @Now
FROM
    "Scope" AS scope
INNER JOIN
    "WorkEditions" AS edition ON edition."PublicId" = @EditionPublicId
INNER JOIN
    "ReaderContent" AS content ON content."WorkEditionId" = edition."Id"
LEFT JOIN
    "WorkChapters" AS chapter ON chapter."Id" = content."WorkChapterId"
WHERE
    chapter."PublicId" IS NOT DISTINCT FROM @ChapterPublicId
    AND content."VariantKey" = @VariantKey
    AND content."ContentRevision" = @ContentRevision
    AND content."PublishedAt" IS NOT NULL
ON CONFLICT ("PublicId") DO NOTHING
RETURNING
    highlight."PublicId",
    highlight."Revision";

-- Logic BOOKMARK_EDIT: public UUID is an address; current membership and expected revision authorize the mutation.
WITH "Actor" AS MATERIALIZED (
    SELECT
        account."Id"
    FROM
        "Accounts" AS account
    WHERE
        account."Id" = @ActorAccountId
        AND account."IsEnabled" = TRUE
    FOR UPDATE OF account
), "Scope" AS MATERIALIZED (
    SELECT
        membership."ProfileId"
    FROM
        "AccountProfiles" AS membership
    INNER JOIN
        "Actor" AS actor ON actor."Id" = membership."AccountId"
    WHERE
        membership."ProfileId" = @ActiveProfileId
    FOR SHARE OF membership
)
UPDATE
    "ReaderBookmarks" AS bookmark
SET
    "Locator" = @Locator,
    "Label" = @Label,
    "Revision" = bookmark."Revision" + 1,
    "UpdatedAt" = @Now
FROM
    "Scope" AS scope
WHERE
    bookmark."PublicId" = @PublicId
    AND bookmark."ProfileId" = scope."ProfileId"
    AND bookmark."Revision" = @ExpectedRevision
    AND @Now >= bookmark."UpdatedAt"
RETURNING
    bookmark."PublicId",
    bookmark."Revision";

-- Logic HIGHLIGHT_EDIT: anchors and extracted selection remain tied to the annotation's immutable revision.
WITH "Actor" AS MATERIALIZED (
    SELECT
        account."Id"
    FROM
        "Accounts" AS account
    WHERE
        account."Id" = @ActorAccountId
        AND account."IsEnabled" = TRUE
    FOR UPDATE OF account
), "Scope" AS MATERIALIZED (
    SELECT
        membership."ProfileId"
    FROM
        "AccountProfiles" AS membership
    INNER JOIN
        "Actor" AS actor ON actor."Id" = membership."AccountId"
    WHERE
        membership."ProfileId" = @ActiveProfileId
    FOR SHARE OF membership
)
UPDATE
    "ReaderHighlights" AS highlight
SET
    "StartLocator" = @StartLocator,
    "EndLocator" = @EndLocator,
    "SelectedText" = @SelectedText,
    "Note" = @Note,
    "Revision" = highlight."Revision" + 1,
    "UpdatedAt" = @Now
FROM
    "Scope" AS scope
WHERE
    highlight."PublicId" = @PublicId
    AND highlight."ProfileId" = scope."ProfileId"
    AND highlight."Revision" = @ExpectedRevision
    AND @Now >= highlight."UpdatedAt"
RETURNING
    highlight."PublicId",
    highlight."Revision";

-- Logic BOOKMARK_DELETE: a stale edit cannot silently delete a newer annotation.
WITH "Actor" AS MATERIALIZED (
    SELECT
        account."Id"
    FROM
        "Accounts" AS account
    WHERE
        account."Id" = @ActorAccountId
        AND account."IsEnabled" = TRUE
    FOR UPDATE OF account
), "Scope" AS MATERIALIZED (
    SELECT
        membership."ProfileId"
    FROM
        "AccountProfiles" AS membership
    INNER JOIN
        "Actor" AS actor ON actor."Id" = membership."AccountId"
    WHERE
        membership."ProfileId" = @ActiveProfileId
    FOR SHARE OF membership
)
DELETE FROM
    "ReaderBookmarks" AS bookmark
USING
    "Scope" AS scope
WHERE
    bookmark."PublicId" = @PublicId
    AND bookmark."ProfileId" = scope."ProfileId"
    AND bookmark."Revision" = @ExpectedRevision
RETURNING
    bookmark."PublicId";

-- Logic HIGHLIGHT_DELETE: no permission derives from knowing the annotation UUID.
WITH "Actor" AS MATERIALIZED (
    SELECT
        account."Id"
    FROM
        "Accounts" AS account
    WHERE
        account."Id" = @ActorAccountId
        AND account."IsEnabled" = TRUE
    FOR UPDATE OF account
), "Scope" AS MATERIALIZED (
    SELECT
        membership."ProfileId"
    FROM
        "AccountProfiles" AS membership
    INNER JOIN
        "Actor" AS actor ON actor."Id" = membership."AccountId"
    WHERE
        membership."ProfileId" = @ActiveProfileId
    FOR SHARE OF membership
)
DELETE FROM
    "ReaderHighlights" AS highlight
USING
    "Scope" AS scope
WHERE
    highlight."PublicId" = @PublicId
    AND highlight."ProfileId" = scope."ProfileId"
    AND highlight."Revision" = @ExpectedRevision
RETURNING
    highlight."PublicId";
