-- Logic START: explicit command, never a GET side effect; type comes from the canonical media owner.
WITH "Actor" AS MATERIALIZED (
    SELECT
        account."Id"
    FROM
        "Accounts" AS account
    INNER JOIN
        "AccountProfiles" AS membership
        ON membership."AccountId" = account."Id" AND membership."ProfileId" = @ActiveProfileId
    WHERE
        account."Id" = @ActorAccountId AND account."IsEnabled" = TRUE
    FOR UPDATE OF account
    FOR SHARE OF membership
), "Root" AS (
    INSERT INTO "MediaProgress" AS progress (
        "ProfileId", "WorkId", "WorkEpisodeId", "WorkChapterId", "WorkTrackId", "WorkEditionId",
        "ProgressPositionTypeId", "CreatedAt", "LastActivityAt")
    SELECT
        @ActiveProfileId,
        work."Id",
        episode."Id",
        chapter."Id",
        track."Id",
        edition."Id",
        @PositionTypeId,
        @Now,
        @Now
    FROM
        "Actor" AS actor
    INNER JOIN
        "Works" AS work ON work."PublicId" = @WorkPublicId
    LEFT JOIN
        "WorkEpisodes" AS episode
        ON episode."PublicId" = @EpisodePublicId AND episode."WorkId" = work."Id"
    LEFT JOIN
        "WorkChapters" AS chapter
        ON chapter."PublicId" = @ChapterPublicId AND chapter."WorkId" = work."Id"
    LEFT JOIN
        "WorkTracks" AS track
        ON track."PublicId" = @TrackPublicId AND track."WorkId" = work."Id"
    LEFT JOIN
        "WorkEditions" AS edition
        ON edition."PublicId" = @EditionPublicId AND edition."WorkId" = work."Id"
    WHERE
        @PositionTypeId IN (1, 2, 3)
        AND num_nonnulls(@EpisodePublicId, @ChapterPublicId, @TrackPublicId, @EditionPublicId) <= 1
        AND (@EpisodePublicId IS NULL OR episode."Id" IS NOT NULL)
        AND (@ChapterPublicId IS NULL OR chapter."Id" IS NOT NULL)
        AND (@TrackPublicId IS NULL OR track."Id" IS NOT NULL)
        AND (@EditionPublicId IS NULL OR edition."Id" IS NOT NULL)
    ON CONFLICT ("ProfileId", "WorkId", "WorkEpisodeId", "WorkChapterId", "WorkTrackId", "WorkEditionId")
        DO NOTHING
    RETURNING
        progress."Id",
        progress."PublicId",
        progress."WorkId",
        progress."Revision",
        progress."ProgressPositionTypeId"
), "TimePosition" AS (
    INSERT INTO "TimeProgressPositions" AS position ("MediaProgressId")
    SELECT
        root."Id"
    FROM
        "Root" AS root
    WHERE
        root."ProgressPositionTypeId" = 1
    RETURNING
        position."MediaProgressId"
), "ReadingPosition" AS (
    INSERT INTO "ReadingProgressPositions" AS position ("MediaProgressId", "WorkId")
    SELECT
        root."Id",
        root."WorkId"
    FROM
        "Root" AS root
    WHERE
        root."ProgressPositionTypeId" = 2
    RETURNING
        position."MediaProgressId"
), "GamePosition" AS (
    INSERT INTO "GameProgressPositions" AS position ("MediaProgressId")
    SELECT
        root."Id"
    FROM
        "Root" AS root
    WHERE
        root."ProgressPositionTypeId" = 3
    RETURNING
        position."MediaProgressId"
)
SELECT
    root."PublicId" AS "MediaProgressId",
    root."Revision"
FROM
    "Root" AS root
WHERE
    EXISTS (SELECT 1 FROM "TimePosition" AS position WHERE position."MediaProgressId" = root."Id")
    OR EXISTS (SELECT 1 FROM "ReadingPosition" AS position WHERE position."MediaProgressId" = root."Id")
    OR EXISTS (SELECT 1 FROM "GamePosition" AS position WHERE position."MediaProgressId" = root."Id");

-- Service TARGET: fresh snapshot after START collision, never a no-op UPDATE used as a read.
SELECT
    progress."PublicId" AS "MediaProgressId",
    progress."Revision"
FROM
    "MediaProgress" AS progress
INNER JOIN
    "Works" AS work ON work."Id" = progress."WorkId"
INNER JOIN
    "AccountProfiles" AS membership ON membership."ProfileId" = progress."ProfileId"
INNER JOIN
    "Accounts" AS account ON account."Id" = membership."AccountId" AND account."IsEnabled" = TRUE
LEFT JOIN
    "WorkEpisodes" AS episode ON episode."Id" = progress."WorkEpisodeId"
LEFT JOIN
    "WorkChapters" AS chapter ON chapter."Id" = progress."WorkChapterId"
LEFT JOIN
    "WorkTracks" AS track ON track."Id" = progress."WorkTrackId"
LEFT JOIN
    "WorkEditions" AS edition ON edition."Id" = progress."WorkEditionId"
WHERE
    account."Id" = @ActorAccountId AND progress."ProfileId" = @ActiveProfileId
    AND work."PublicId" = @WorkPublicId AND progress."ProgressPositionTypeId" = @PositionTypeId
    AND episode."PublicId" IS NOT DISTINCT FROM @EpisodePublicId
    AND chapter."PublicId" IS NOT DISTINCT FROM @ChapterPublicId
    AND track."PublicId" IS NOT DISTINCT FROM @TrackPublicId
    AND edition."PublicId" IS NOT DISTINCT FROM @EditionPublicId;

-- Logic CHECKPOINT: one owner for live/offline/manual Time, Reading and Game updates.
-- Account/profile context, source, completion reason, hash and Now are server-validated.
WITH "Actor" AS MATERIALIZED (
    SELECT
        account."Id"
    FROM
        "Accounts" AS account
    INNER JOIN
        "AccountProfiles" AS membership
        ON membership."AccountId" = account."Id" AND membership."ProfileId" = @ActiveProfileId
    WHERE
        account."Id" = @ActorAccountId AND account."IsEnabled" = TRUE
    FOR UPDATE OF account
    FOR SHARE OF membership
), "Content" AS MATERIALIZED (
    SELECT
        content."Id",
        content."WorkId",
        content."WorkEditionId",
        content."WorkChapterId"
    FROM
        "ReaderContent" AS content
    INNER JOIN
        "WorkEditions" AS edition ON edition."Id" = content."WorkEditionId"
    INNER JOIN
        "MediaProgress" AS progress ON progress."PublicId" = @ProgressPublicId
            AND progress."ProfileId" = @ActiveProfileId
            AND progress."WorkChapterId" IS NOT DISTINCT FROM content."WorkChapterId"
    WHERE
        edition."PublicId" = @ReaderEditionPublicId
        AND content."ContentRevision" = @ContentRevision
        AND content."VariantKey" = @VariantKey
        AND content."PublishedAt" IS NOT NULL
), "Target" AS MATERIALIZED (
    SELECT
        progress."Id",
        progress."ProfileId",
        progress."Revision",
        progress."ProgressPositionTypeId",
        content."Id" AS "ReaderContentId"
    FROM
        "MediaProgress" AS progress
    INNER JOIN
        "Actor" AS actor ON TRUE
    LEFT JOIN
        "Content" AS content
        ON content."WorkId" = progress."WorkId"
        AND (progress."WorkEditionId" IS NULL OR progress."WorkEditionId" = content."WorkEditionId")
        AND progress."WorkChapterId" IS NOT DISTINCT FROM content."WorkChapterId"
    WHERE
        progress."PublicId" = @ProgressPublicId
        AND progress."ProfileId" = @ActiveProfileId
        AND progress."Revision" = @ExpectedRevision
        AND @Now >= progress."CreatedAt"
        AND @SourceTypeId IN (1, 2, 3)
        AND (@SourceTypeId <> 3 OR @CompletionReasonTypeId IS NULL OR @CompletionReasonTypeId = 3)
        AND (@CompletionReasonTypeId IS NULL
            OR @CompletionReasonTypeId = 3 AND @SourceTypeId = 3
            OR @CompletionReasonTypeId = 1 AND progress."ProgressPositionTypeId" = 1
            OR @CompletionReasonTypeId = 2 AND progress."ProgressPositionTypeId" = 2)
        AND (NOT @ClearCompletion OR @SourceTypeId = 3 AND @CompletionReasonTypeId IS NULL)
        AND CASE progress."ProgressPositionTypeId"
            WHEN 1 THEN @PositionMs IS NOT NULL AND @PositionMs >= 0
                AND (@DurationMs IS NULL OR @DurationMs >= 0)
                AND num_nonnulls(@ReaderEditionPublicId, @ContentRevision, @VariantKey, @Locator,
                    @ProgressPermille, @PlayedMs, @ProgressPercent) = 0
            WHEN 2 THEN num_nonnulls(@PositionMs, @DurationMs, @PlayedMs, @ProgressPercent) = 0
                AND ((num_nonnulls(@ReaderEditionPublicId, @ContentRevision, @VariantKey,
                        @Locator, @ProgressPermille) = 0)
                    OR (content."Id" IS NOT NULL AND @Locator IS NOT NULL
                        AND (@ProgressPermille IS NULL OR @ProgressPermille BETWEEN 0 AND 1000)
                        AND "IsReaderLocatorValid"(content."Id", @Locator)))
            WHEN 3 THEN @PlayedMs IS NOT NULL AND @PlayedMs >= 0
                AND (@ProgressPercent IS NULL OR @ProgressPercent BETWEEN 0 AND 100)
                AND num_nonnulls(@PositionMs, @DurationMs, @ReaderEditionPublicId, @ContentRevision,
                    @VariantKey, @Locator, @ProgressPermille) = 0
            ELSE FALSE
        END
    FOR UPDATE OF progress
), "Receipt" AS (
    INSERT INTO "MediaProgressCheckpointEvents" AS receipt (
        "ProfileId", "MediaProgressId", "ClientEventId", "AcceptedRevision", "ActorAccountId",
        "ProgressCheckpointSourceTypeId", "PayloadHash", "ClientOccurredAt", "AcceptedAt",
        "CompletionReasonTypeId", "ClearsCompletion")
    SELECT
        target."ProfileId",
        target."Id",
        @ClientEventId,
        target."Revision" + 1,
        @ActorAccountId,
        @SourceTypeId,
        @PayloadHash,
        @ClientOccurredAt,
        @Now,
        @CompletionReasonTypeId,
        @ClearCompletion
    FROM
        "Target" AS target
    ON CONFLICT ("ProfileId", "ClientEventId") DO NOTHING
    RETURNING
        receipt."MediaProgressId",
        receipt."AcceptedRevision"
), "Accepted" AS (
    UPDATE
        "MediaProgress" AS progress
    SET
        "Revision" = receipt."AcceptedRevision",
        "LastActivityAt" = @Now,
        "IsCompleted" = NOT @ClearCompletion AND (progress."IsCompleted" OR @CompletionReasonTypeId IS NOT NULL),
        "CompletionReasonTypeId" = CASE
            WHEN @ClearCompletion THEN NULL
            WHEN progress."IsCompleted" THEN progress."CompletionReasonTypeId"
            ELSE @CompletionReasonTypeId END,
        "CompletedAt" = CASE
            WHEN @ClearCompletion THEN NULL
            WHEN progress."IsCompleted" THEN progress."CompletedAt"
            WHEN @CompletionReasonTypeId IS NOT NULL THEN @Now END,
        "CompletedByAccountId" = CASE
            WHEN @ClearCompletion THEN NULL
            WHEN progress."IsCompleted" THEN progress."CompletedByAccountId"
            WHEN @CompletionReasonTypeId IS NOT NULL THEN @ActorAccountId END
    FROM
        "Receipt" AS receipt
    WHERE
        progress."Id" = receipt."MediaProgressId"
    RETURNING
        progress."Id",
        progress."PublicId",
        progress."Revision",
        progress."IsCompleted"
), "TimePosition" AS (
    UPDATE
        "TimeProgressPositions" AS position
    SET
        "PositionMs" = @PositionMs,
        "DurationMs" = @DurationMs
    FROM
        "Accepted" AS accepted
    WHERE
        position."MediaProgressId" = accepted."Id"
    RETURNING
        position."MediaProgressId"
), "ReadingPosition" AS (
    UPDATE
        "ReadingProgressPositions" AS position
    SET
        "ReaderContentId" = target."ReaderContentId",
        "Locator" = @Locator,
        "ProgressPermille" = @ProgressPermille
    FROM
        "Accepted" AS accepted
    INNER JOIN
        "Target" AS target ON target."Id" = accepted."Id"
    WHERE
        position."MediaProgressId" = accepted."Id"
    RETURNING
        position."MediaProgressId"
), "GamePosition" AS (
    UPDATE
        "GameProgressPositions" AS position
    SET
        "PlayedMs" = @PlayedMs,
        "ProgressPercent" = @ProgressPercent
    FROM
        "Accepted" AS accepted
    WHERE
        position."MediaProgressId" = accepted."Id"
    RETURNING
        position."MediaProgressId"
)
SELECT
    accepted."PublicId" AS "MediaProgressId",
    accepted."Revision",
    accepted."IsCompleted"
FROM
    "Accepted" AS accepted
WHERE
    EXISTS (SELECT 1 FROM "TimePosition" AS position WHERE position."MediaProgressId" = accepted."Id")
    OR EXISTS (SELECT 1 FROM "ReadingPosition" AS position WHERE position."MediaProgressId" = accepted."Id")
    OR EXISTS (SELECT 1 FROM "GamePosition" AS position WHERE position."MediaProgressId" = accepted."Id");

-- Service RECEIPT: exact-payload replay under current profile authorization; no DML.
SELECT
    receipt."AcceptedRevision",
    progress."Revision" AS "CurrentRevision"
FROM
    "MediaProgressCheckpointEvents" AS receipt
INNER JOIN
    "MediaProgress" AS progress ON progress."Id" = receipt."MediaProgressId"
INNER JOIN
    "AccountProfiles" AS membership ON membership."ProfileId" = receipt."ProfileId"
INNER JOIN
    "Accounts" AS account ON account."Id" = membership."AccountId" AND account."IsEnabled" = TRUE
WHERE
    account."Id" = @ActorAccountId AND receipt."ProfileId" = @ActiveProfileId
    AND receipt."ClientEventId" = @ClientEventId AND progress."PublicId" = @ProgressPublicId
    AND receipt."PayloadHash" = @PayloadHash;

-- Service SNAPSHOT: current exact target/position after conflict or replay, no write and no host path.
SELECT
    progress."PublicId" AS "MediaProgressId",
    work."PublicId" AS "WorkId",
    episode."PublicId" AS "WorkEpisodeId",
    chapter."PublicId" AS "WorkChapterId",
    track."PublicId" AS "WorkTrackId",
    edition."PublicId" AS "WorkEditionId",
    progress."Revision",
    progress."ProgressPositionTypeId",
    progress."LastActivityAt",
    progress."IsCompleted",
    progress."CompletionReasonTypeId",
    progress."CompletedAt",
    completed_by."PublicId" AS "CompletedByAccountId",
    time_position."PositionMs",
    time_position."DurationMs",
    reader_edition."PublicId" AS "ReaderEditionId",
    content."ContentRevision",
    content."VariantKey",
    reading_position."PageIndex",
    reading_position."Locator",
    reading_position."ProgressPermille",
    game_position."PlayedMs",
    game_position."ProgressPercent"
FROM
    "MediaProgress" AS progress
INNER JOIN
    "AccountProfiles" AS membership ON membership."ProfileId" = progress."ProfileId"
INNER JOIN
    "Accounts" AS account ON account."Id" = membership."AccountId" AND account."IsEnabled" = TRUE
INNER JOIN
    "Works" AS work ON work."Id" = progress."WorkId"
LEFT JOIN
    "WorkEpisodes" AS episode ON episode."Id" = progress."WorkEpisodeId"
LEFT JOIN
    "WorkChapters" AS chapter ON chapter."Id" = progress."WorkChapterId"
LEFT JOIN
    "WorkTracks" AS track ON track."Id" = progress."WorkTrackId"
LEFT JOIN
    "WorkEditions" AS edition ON edition."Id" = progress."WorkEditionId"
LEFT JOIN
    "Accounts" AS completed_by ON completed_by."Id" = progress."CompletedByAccountId"
LEFT JOIN
    "TimeProgressPositions" AS time_position ON time_position."MediaProgressId" = progress."Id"
LEFT JOIN
    "ReadingProgressPositions" AS reading_position ON reading_position."MediaProgressId" = progress."Id"
LEFT JOIN
    "ReaderContent" AS content ON content."Id" = reading_position."ReaderContentId"
LEFT JOIN
    "WorkEditions" AS reader_edition ON reader_edition."Id" = content."WorkEditionId"
LEFT JOIN
    "GameProgressPositions" AS game_position ON game_position."MediaProgressId" = progress."Id"
WHERE
    progress."PublicId" = @ProgressPublicId AND progress."ProfileId" = @ActiveProfileId
    AND account."Id" = @ActorAccountId;

-- Service COMPLETION: contiguous regular-episode prefix, not the furthest completed row.
WITH "AuthorizedWork" AS MATERIALIZED (
    SELECT
        work."Id",
        work."PublicId"
    FROM
        "Works" AS work
    INNER JOIN
        "AccountProfiles" AS membership ON membership."ProfileId" = @ActiveProfileId
    INNER JOIN
        "Accounts" AS account ON account."Id" = membership."AccountId" AND account."IsEnabled" = TRUE
    WHERE
        work."PublicId" = @WorkPublicId AND work."MediaTypeId" = 2 AND account."Id" = @ActorAccountId
), "RegularEpisodes" AS MATERIALIZED (
    SELECT
        episode."PublicId",
        season."SeasonNumber",
        episode."EpisodeNumber",
        episode."OrderIndex",
        episode."Id",
        progress."LastActivityAt",
        bool_and(COALESCE(progress."IsCompleted", FALSE)) OVER (
            ORDER BY season."SeasonNumber", episode."EpisodeNumber", episode."OrderIndex", episode."Id"
            ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS "PrefixCompleted"
    FROM
        "AuthorizedWork" AS work
    INNER JOIN
        "WorkEpisodes" AS episode ON episode."WorkId" = work."Id"
    INNER JOIN
        "WorkSeasons" AS season ON season."Id" = episode."WorkSeasonId"
    LEFT JOIN
        "MediaProgress" AS progress
        ON progress."WorkEpisodeId" = episode."Id" AND progress."ProfileId" = @ActiveProfileId
        AND progress."WorkId" = work."Id"
    WHERE
        NOT episode."IsSpecial" AND season."SeasonNumber" > 0 AND episode."EpisodeNumber" > 0
)
SELECT
    work."PublicId" AS "WorkId",
    activity."UpdatedAt",
    completed."PublicId" AS "CompletedThroughEpisodeId",
    completed."SeasonNumber",
    completed."EpisodeNumber"
FROM
    "AuthorizedWork" AS work
CROSS JOIN LATERAL (
    SELECT
        max(episode."LastActivityAt") AS "UpdatedAt"
    FROM
        "RegularEpisodes" AS episode
) AS activity
LEFT JOIN
    LATERAL (
    SELECT
        episode."PublicId",
        episode."SeasonNumber",
        episode."EpisodeNumber"
    FROM
        "RegularEpisodes" AS episode
    WHERE
        episode."PrefixCompleted"
    ORDER BY episode."SeasonNumber" DESC, episode."EpisodeNumber" DESC, episode."OrderIndex" DESC, episode."Id" DESC
    LIMIT 1
) AS completed ON TRUE
WHERE
    activity."UpdatedAt" IS NOT NULL;
