-- Service CONTENT: exact published descriptor, never a filesystem path or implicit parse command.
SELECT
    work."PublicId" AS "WorkId",
    edition."PublicId" AS "WorkEditionId",
    chapter."PublicId" AS "WorkChapterId",
    content."VariantKey",
    content."ContentRevision",
    content."ReaderVariantTypeId",
    content."ReaderLayoutTypeId",
    content."LanguageTag",
    content."PageDirectionTypeId",
    content."TextDirectionTypeId",
    content."WritingModeTypeId",
    content."HasTextLayer",
    content."ContentHash",
    content."NodeCount",
    content."PageCount",
    content."PublishedAt"
FROM
    "ReaderContent" AS content
INNER JOIN
    "WorkEditions" AS edition ON edition."Id" = content."WorkEditionId"
INNER JOIN
    "Works" AS work ON work."Id" = content."WorkId"
INNER JOIN
    "AccountProfiles" AS membership ON membership."ProfileId" = @ActiveProfileId
INNER JOIN
    "Accounts" AS account ON account."Id" = membership."AccountId" AND account."IsEnabled" = TRUE
LEFT JOIN
    "WorkChapters" AS chapter ON chapter."Id" = content."WorkChapterId"
WHERE
    account."Id" = @ActorAccountId
    AND edition."PublicId" = @EditionPublicId
    AND chapter."PublicId" IS NOT DISTINCT FROM @ChapterPublicId
    AND content."VariantKey" = @VariantKey
    AND content."ContentRevision" = @ContentRevision
    AND content."PublishedAt" IS NOT NULL;

-- Service NODES: immutable sequential reader-feed cursor, bounded before source-node enrichment.
WITH "Scope" AS MATERIALIZED (
    SELECT
        content."Id"
    FROM
        "ReaderContent" AS content
    INNER JOIN
        "WorkEditions" AS edition ON edition."Id" = content."WorkEditionId"
    INNER JOIN
        "AccountProfiles" AS membership ON membership."ProfileId" = @ActiveProfileId
    INNER JOIN
        "Accounts" AS account ON account."Id" = membership."AccountId" AND account."IsEnabled" = TRUE
    LEFT JOIN
        "WorkChapters" AS chapter ON chapter."Id" = content."WorkChapterId"
    WHERE
        account."Id" = @ActorAccountId
        AND edition."PublicId" = @EditionPublicId
        AND chapter."PublicId" IS NOT DISTINCT FROM @ChapterPublicId
        AND content."VariantKey" = @VariantKey
        AND content."ContentRevision" = @ContentRevision
        AND content."PublishedAt" IS NOT NULL
        AND @StartOrderIndex >= 0
        AND @PageSize BETWEEN 1 AND 100
), "PageRoots" AS MATERIALIZED (
    SELECT
        node."Id",
        node."StableId",
        node."OrderIndex",
        node."ReaderNodeTypeId",
        node."ReaderTextBlockTypeId",
        node."CharacterCount",
        node."HeadingLevel",
        node."SourceNodeId",
        node."SourcePageIndex",
        node."SourceRegion"
    FROM
        "Scope" AS scope
    INNER JOIN LATERAL (
        SELECT
            node."Id",
            node."StableId",
            node."OrderIndex",
            node."ReaderNodeTypeId",
            node."ReaderTextBlockTypeId",
            node."CharacterCount",
            node."HeadingLevel",
            node."SourceNodeId",
            node."SourcePageIndex",
            node."SourceRegion"
        FROM
            "ReaderContentNodes" AS node
        WHERE
            node."ReaderContentId" = scope."Id"
            AND node."OrderIndex" >= @StartOrderIndex
        ORDER BY
            node."OrderIndex"
        LIMIT @PageSize
    ) AS node ON TRUE
)
SELECT
    node."StableId",
    node."OrderIndex",
    node."ReaderNodeTypeId",
    node."ReaderTextBlockTypeId",
    node."CharacterCount",
    node."HeadingLevel",
    source_node."StableId" AS "SourceStableId",
    node."SourcePageIndex",
    node."SourceRegion"
FROM
    "PageRoots" AS node
LEFT JOIN LATERAL (
    SELECT
        source."StableId"
    FROM
        "ReaderContentNodes" AS source
    WHERE
        node."SourceNodeId" IS NOT NULL
        AND source."Id" = node."SourceNodeId"
    LIMIT 1
) AS source_node ON TRUE
ORDER BY
    node."OrderIndex",
    node."Id";

-- Internal Service SOURCE: only the authorized delivery adapter may consume this private projection.
SELECT
    content."StorageKey",
    content."ContentHash",
    content."ReaderLayoutTypeId"
FROM
    "ReaderContent" AS content
INNER JOIN
    "WorkEditions" AS edition ON edition."Id" = content."WorkEditionId"
INNER JOIN
    "AccountProfiles" AS membership ON membership."ProfileId" = @ActiveProfileId
INNER JOIN
    "Accounts" AS account ON account."Id" = membership."AccountId" AND account."IsEnabled" = TRUE
LEFT JOIN
    "WorkChapters" AS chapter ON chapter."Id" = content."WorkChapterId"
WHERE
    account."Id" = @ActorAccountId
    AND edition."PublicId" = @EditionPublicId
    AND chapter."PublicId" IS NOT DISTINCT FROM @ChapterPublicId
    AND content."VariantKey" = @VariantKey
    AND content."ContentRevision" = @ContentRevision
    AND content."PublishedAt" IS NOT NULL;

-- Logic PUBLISH: the payload was hashed/staged outside SQL; only the current claimed builder can publish.
WITH "Builder" AS MATERIALIZED (
    SELECT
        operation."Id",
        operation."OperationKindKey"
    FROM
        "Operations" AS operation
    INNER JOIN
        "ReaderContent" AS content ON content."BuilderOperationId" = operation."Id"
    WHERE
        content."Id" = @ReaderContentId
        AND operation."OperationStatusTypeId" = 2
        AND operation."ClaimedBy" = @TrustedWorkerKey
        AND operation."ClaimedUntil" > @Now
    FOR UPDATE OF operation
)
UPDATE
    "ReaderContent" AS content
SET
    "PublishedAt" = @Now
FROM
    "Builder" AS operation
WHERE
    content."Id" = @ReaderContentId
    AND content."BuilderOperationId" = operation."Id"
    AND content."PublishedAt" IS NULL
    AND content."ContentHash" = @ExpectedContentHash
    AND @Now >= content."CreatedAt"
    AND ((content."ReaderVariantTypeId" IN (1, 2) AND operation."OperationKindKey" = 'reader_parse')
        OR (content."ReaderVariantTypeId" = 3 AND operation."OperationKindKey" = 'reader_translate'))
RETURNING
    content."Id";
