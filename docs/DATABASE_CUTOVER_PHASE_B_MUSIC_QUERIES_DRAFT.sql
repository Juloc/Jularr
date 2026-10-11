-- Service ARTISTS: names and identity belong to People, not a second music-owned root.
SELECT
    person."PublicId" AS "ArtistPublicId",
    person."DisplayName",
    person."SortName",
    artist."CreatedAt",
    artist."LastRefreshedAt"
FROM
    "People" AS person
INNER JOIN
    "MusicArtists" AS artist ON artist."Id" = person."Id"
WHERE
    EXISTS (
        SELECT
            1
        FROM
            "Accounts" AS account
        INNER JOIN
            "AccountProfiles" AS membership ON membership."AccountId" = account."Id"
        WHERE
            account."Id" = @ActorAccountId
            AND account."IsEnabled" = TRUE
            AND membership."ProfileId" = @ActiveProfileId
    )
    AND @PageSize BETWEEN 1 AND 100
    AND @Offset BETWEEN 0 AND 100000
ORDER BY
    person."SortName",
    person."Id"
LIMIT @PageSize OFFSET @Offset;

-- Service ARTIST: an opaque UUID is an address, never proof of profile access.
SELECT
    person."PublicId" AS "ArtistPublicId",
    person."DisplayName",
    person."SortName",
    artist."CreatedAt",
    artist."LastRefreshedAt"
FROM
    "People" AS person
INNER JOIN
    "MusicArtists" AS artist ON artist."Id" = person."Id"
INNER JOIN
    "AccountProfiles" AS membership ON membership."ProfileId" = @ActiveProfileId
INNER JOIN
    "Accounts" AS account ON account."Id" = membership."AccountId"
WHERE
    account."Id" = @ActorAccountId
    AND account."IsEnabled" = TRUE
    AND person."PublicId" = @ArtistPublicId;

-- Service RECORDINGS: shared recording facts, not album placements or physical files.
WITH "Scope" AS MATERIALIZED (
    SELECT
        artist."Id"
    FROM
        "People" AS person
    INNER JOIN
        "MusicArtists" AS artist ON artist."Id" = person."Id"
    INNER JOIN
        "AccountProfiles" AS membership ON membership."ProfileId" = @ActiveProfileId
    INNER JOIN
        "Accounts" AS account ON account."Id" = membership."AccountId"
    WHERE
        person."PublicId" = @ArtistPublicId
        AND account."Id" = @ActorAccountId
        AND account."IsEnabled" = TRUE
        AND @PageSize BETWEEN 1 AND 100
        AND @Offset BETWEEN 0 AND 100000
), "PageRoots" AS MATERIALIZED (
    SELECT
        credit."MusicRecordingId",
        credit."CreditOrder",
        credit."CreditName"
    FROM
        "Scope" AS scope
    INNER JOIN LATERAL (
        SELECT
            credit."MusicRecordingId",
            credit."CreditOrder",
            credit."CreditName"
        FROM
            "MusicRecordingArtists" AS credit
        WHERE
            credit."MusicArtistId" = scope."Id"
        ORDER BY
            credit."MusicRecordingId"
        LIMIT @PageSize OFFSET @Offset
    ) AS credit ON TRUE
)
SELECT
    recording."PublicId" AS "RecordingPublicId",
    recording."DisplayName",
    recording."DurationMs",
    credit."CreditOrder",
    credit."CreditName"
FROM
    "PageRoots" AS credit
INNER JOIN LATERAL (
    SELECT
        recording."PublicId",
        recording."DisplayName",
        recording."DurationMs"
    FROM
        "MusicRecordings" AS recording
    WHERE
        recording."Id" = credit."MusicRecordingId"
    LIMIT 1
) AS recording ON TRUE
ORDER BY
    credit."MusicRecordingId";
