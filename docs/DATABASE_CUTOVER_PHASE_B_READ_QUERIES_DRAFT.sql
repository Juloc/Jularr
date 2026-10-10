-- Scope and paging parameters come from the authorized ServiceContext.

-- 1. One SELECT for Profile Watchlist WorkCards, roots paged BEFORE LATERAL children.
WITH "PageRoots" AS MATERIALIZED (
    SELECT
        watchlist_entry."WorkId",
        watchlist_entry."AddedAt"
    FROM
        "WatchlistEntries" AS watchlist_entry
    INNER JOIN
        "AccountProfiles" AS account_profile
        ON account_profile."ProfileId" = watchlist_entry."ProfileId"
        AND account_profile."AccountId" = @ActorAccountId
    INNER JOIN
        "Accounts" AS account
        ON account."Id" = account_profile."AccountId"
        AND account."IsEnabled" = TRUE
    WHERE
        watchlist_entry."ProfileId" = @ActiveProfileId
    ORDER BY
        watchlist_entry."AddedAt" DESC,
        watchlist_entry."WorkId" DESC
    LIMIT @PageSize
    OFFSET @Offset
)
SELECT
    work."PublicId" AS "WorkId",
    page_root."AddedAt",
    work."MediaTypeId",
    COALESCE(title_match."Title", work."CanonicalTitle") AS "DisplayName",
    cover_image."ImageId" AS "CoverImageId",
    banner_image."ImageId" AS "BannerImageId",
    COALESCE(availability."HasLocalFile", FALSE) AS "HasLocalFile",
    COALESCE(audio_languages."Languages", '[]'::jsonb) AS "AudioLanguages",
    COALESCE(subtitle_languages."Languages", '[]'::jsonb) AS "SubtitleLanguages",
    COALESCE(progress."IsCompleted", FALSE) AS "IsCompleted",
    progress."LastActivityAt"
FROM
    "PageRoots" AS page_root
INNER JOIN
    "Works" AS work ON work."Id" = page_root."WorkId"
LEFT JOIN LATERAL (
    SELECT
        title."Title"
    FROM
        "WorkTitles" AS title
    LEFT JOIN
        "UiLocales" AS locale ON locale."Id" = title."UiLocaleId"
    WHERE
        title."WorkId" = work."Id"
    ORDER BY
        CASE
            WHEN locale."Locale" = @UiLocale THEN 0
            WHEN locale."Locale" = split_part(@UiLocale, '-', 1) THEN 1
            WHEN locale."Locale" = 'en' THEN 2
            WHEN title."IsOriginal" THEN 3
            ELSE 4
        END,
        title."IsPreferred" DESC,
        title."Id"
    LIMIT 1
) AS title_match ON TRUE
LEFT JOIN LATERAL (
    SELECT
        image."PublicId" AS "ImageId"
    FROM
        "ImageAssignments" AS assignment
    INNER JOIN
        "Images" AS image ON image."Id" = assignment."ImageId"
    INNER JOIN
        "ImageTypes" AS image_type ON image_type."Id" = assignment."ImageTypeId"
    LEFT JOIN
        "UiLocales" AS locale ON locale."Id" = assignment."UiLocaleId"
    WHERE
        assignment."WorkId" = work."Id"
      AND image_type."Key" = 'cover'
    ORDER BY
        assignment."IsManual" DESC,
        CASE
            WHEN locale."Locale" = @UiLocale THEN 0
            WHEN locale."Locale" = 'en' THEN 1
            ELSE 2
        END,
        assignment."Priority" DESC,
        assignment."Id"
    LIMIT 1
) AS cover_image ON TRUE
LEFT JOIN LATERAL (
    SELECT
        image."PublicId" AS "ImageId"
    FROM
        "ImageAssignments" AS assignment
    INNER JOIN
        "Images" AS image ON image."Id" = assignment."ImageId"
    INNER JOIN
        "ImageTypes" AS image_type ON image_type."Id" = assignment."ImageTypeId"
    WHERE
        assignment."WorkId" = work."Id"
      AND image_type."Key" = 'banner'
    ORDER BY
        assignment."IsManual" DESC,
        assignment."Priority" DESC,
        assignment."Id"
    LIMIT 1
) AS banner_image ON TRUE
LEFT JOIN LATERAL (
    SELECT
        EXISTS (
            SELECT
                1
            FROM
                "WorkEditions" AS edition
            INNER JOIN
                "WorkVersions" AS version ON version."WorkEditionId" = edition."Id"
            INNER JOIN
                "MediaAssets" AS asset ON asset."WorkVersionId" = version."Id"
            INNER JOIN
                "StoredFiles" AS file ON file."MediaAssetId" = asset."Id"
            WHERE
                edition."WorkId" = work."Id"
                AND file."IsPresent" = TRUE
        ) AS "HasLocalFile"
) AS availability ON TRUE
LEFT JOIN LATERAL (
    SELECT
        jsonb_agg(a."Language" ORDER BY a."Language") AS "Languages"
    FROM (
        SELECT DISTINCT
            track."Language"
        FROM
            "WorkEditions" AS edition
        INNER JOIN
            "WorkVersions" AS version ON version."WorkEditionId" = edition."Id"
        INNER JOIN
            "MediaAssets" AS asset ON asset."WorkVersionId" = version."Id"
        INNER JOIN
            "StoredFiles" AS file ON file."MediaAssetId" = asset."Id" AND file."IsPresent" = TRUE
        INNER JOIN
            "MediaTracks" AS track ON track."StoredFileId" = file."Id"
        INNER JOIN
            "MediaTrackTypes" AS track_type ON track_type."Id" = track."MediaTrackTypeId"
        WHERE
            edition."WorkId" = work."Id"
          AND track_type."Key" = 'audio'
          AND track."Language" IS NOT NULL
        ORDER BY
            track."Language"
        LIMIT 32
    ) AS a
) AS audio_languages ON TRUE
LEFT JOIN LATERAL (
    SELECT
        jsonb_agg(s."Language" ORDER BY s."Language") AS "Languages"
    FROM (
        SELECT DISTINCT
            track."Language"
        FROM
            "WorkEditions" AS edition
        INNER JOIN
            "WorkVersions" AS version ON version."WorkEditionId" = edition."Id"
        INNER JOIN
            "MediaAssets" AS asset ON asset."WorkVersionId" = version."Id"
        INNER JOIN
            "StoredFiles" AS file ON file."MediaAssetId" = asset."Id" AND file."IsPresent" = TRUE
        INNER JOIN
            "MediaTracks" AS track ON track."StoredFileId" = file."Id"
        INNER JOIN
            "MediaTrackTypes" AS track_type ON track_type."Id" = track."MediaTrackTypeId"
        WHERE
            edition."WorkId" = work."Id"
          AND track_type."Key" = 'subtitle'
          AND track."Language" IS NOT NULL
        ORDER BY
            track."Language"
        LIMIT 32
    ) AS s
) AS subtitle_languages ON TRUE
LEFT JOIN LATERAL (
    SELECT
        media_progress."IsCompleted",
        media_progress."LastActivityAt"
    FROM
        "MediaProgress" AS media_progress
    WHERE
        media_progress."ProfileId" = @ActiveProfileId
      AND media_progress."WorkId" = work."Id"
    ORDER BY
        media_progress."LastActivityAt" DESC,
        media_progress."Id" DESC
    LIMIT 1
) AS progress ON TRUE
ORDER BY
    page_root."AddedAt" DESC,
    page_root."WorkId" DESC;

-- 2. Bounded Continue list, never cross-profile, deterministic ordering.
SELECT
    progress."PublicId" AS "MediaProgressId",
    work."PublicId" AS "WorkId",
    episode."PublicId" AS "WorkEpisodeId",
    chapter."PublicId" AS "WorkChapterId",
    track."PublicId" AS "WorkTrackId",
    edition."PublicId" AS "WorkEditionId",
    progress."ProgressPositionTypeId",
    progress."Revision",
    progress."LastActivityAt"
FROM
    "MediaProgress" AS progress
INNER JOIN
    "AccountProfiles" AS account_profile
    ON account_profile."ProfileId" = progress."ProfileId"
    AND account_profile."AccountId" = @ActorAccountId
INNER JOIN
    "Accounts" AS account
    ON account."Id" = account_profile."AccountId"
    AND account."IsEnabled" = TRUE
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
WHERE
    progress."ProfileId" = @ActiveProfileId
    AND progress."IsCompleted" = FALSE
ORDER BY
    progress."LastActivityAt" DESC,
    progress."Id" DESC
LIMIT @PageSize
OFFSET @Offset;

-- 3. Logic-only short transaction: atomically claim due queued jobs.
-- @PendingStatusTypeId, @RunningStatusTypeId MUST be trusted server enum IDs,
-- never arbitrary client strings. Start/commit is owned by Service/short job unit.
WITH claimed AS (
    SELECT
        operation."Id"
    FROM
        "Operations" AS operation
    WHERE
        operation."OperationStatusTypeId" = @PendingStatusTypeId
      AND (operation."NextAttemptAt" IS NULL OR operation."NextAttemptAt" <= now())
      AND (operation."ClaimedUntil" IS NULL OR operation."ClaimedUntil" < now())
    ORDER BY
        operation."NextAttemptAt" NULLS FIRST,
        operation."CreatedAt",
        operation."Id"
    FOR UPDATE OF operation SKIP LOCKED
    LIMIT @BatchSize
)
UPDATE
    "Operations" AS operation
SET
    "OperationStatusTypeId" = @RunningStatusTypeId,
    "ClaimedBy" = @TrustedWorkerKey,
    "ClaimedUntil" = now() + INTERVAL '2 minutes',
    "StartedAt" = COALESCE(operation."StartedAt", now()),
    "AttemptCount" = operation."AttemptCount" + 1
FROM
    claimed
WHERE
    operation."Id" = claimed."Id"
RETURNING
    operation."Id",
    operation."OperationKindKey",
    operation."Input",
    operation."AttemptCount";

-- 4. Logic-only bounded optimistic checkpoint revision gate (B03).
-- This UPDATE is one statement INSIDE a shared SQL transaction containing
-- the corresponding Time/Reading/Game detail mutation and immutable
-- MediaProgressCheckpointEvents row. The service must check the event ID first,
-- then claim this exact expected revision; no stale writer may overwrite newer
-- progress or Completed state. Event replay returns its prior result, not DML.
-- @ActiveProfileId must come from validated ServiceContext, never raw DTO.
-- 0 updated rows = conflict/not found; the caller may not treat it as success.
UPDATE
    "MediaProgress" AS progress
SET
    "Revision" = progress."Revision" + 1,
    "LastActivityAt" = now()
WHERE
    progress."Id" = @MediaProgressId
  AND progress."ProfileId" = @ActiveProfileId
  AND progress."Revision" = @ExpectedRevision
  AND EXISTS (
      SELECT
          1
      FROM
          "AccountProfiles" AS account_profile
      INNER JOIN
          "Accounts" AS account
          ON account."Id" = account_profile."AccountId"
      WHERE
          account_profile."AccountId" = @ActorAccountId
        AND account_profile."ProfileId" = progress."ProfileId"
        AND account."IsEnabled" = TRUE
  )
RETURNING
    progress."Id",
    progress."Revision";
