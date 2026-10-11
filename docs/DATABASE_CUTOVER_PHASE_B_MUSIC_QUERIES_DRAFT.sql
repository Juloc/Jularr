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

-- Service ALBUMS: page canonical Works before metadata and release projection.
WITH "Scope" AS MATERIALIZED (
    SELECT
        person."Id"
    FROM
        "People" AS person
    WHERE
        person."PublicId" = @ArtistPublicId
        AND @PageSize BETWEEN 1 AND 100
        AND @Offset BETWEEN 0 AND 100000
        AND EXISTS (
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
), "PageRoots" AS MATERIALIZED (
    SELECT
        link."WorkId"
    FROM
        "Scope" AS scope
    INNER JOIN LATERAL (
        SELECT
            link."WorkId"
        FROM
            "WorkMusicArtists" AS link
        WHERE
            link."MusicArtistId" = scope."Id"
        ORDER BY
            link."WorkId" ASC
        LIMIT @PageSize OFFSET @Offset
    ) AS link ON TRUE
)
SELECT
    work."PublicId" AS "WorkPublicId",
    work."CanonicalTitle",
    album."MusicAlbumTypeId",
    facts."FirstPublishedOn",
    edition."PublicId" AS "TrackListEditionPublicId"
FROM
    "PageRoots" AS root
INNER JOIN LATERAL (
    SELECT
        work."PublicId",
        work."CanonicalTitle"
    FROM
        "Works" AS work
    WHERE
        work."Id" = root."WorkId"
    LIMIT 1
) AS work ON TRUE
INNER JOIN LATERAL (
    SELECT
        album."MusicAlbumTypeId",
        album."TrackListWorkEditionId"
    FROM
        "MusicAlbums" AS album
    WHERE
        album."WorkId" = root."WorkId"
    LIMIT 1
) AS album ON TRUE
LEFT JOIN LATERAL (
    SELECT
        facts."FirstPublishedOn"
    FROM
        "WorkMetadataFacts" AS facts
    WHERE
        facts."WorkId" = root."WorkId"
    LIMIT 1
) AS facts ON TRUE
LEFT JOIN LATERAL (
    SELECT
        edition."PublicId"
    FROM
        "WorkEditions" AS edition
    WHERE
        album."TrackListWorkEditionId" IS NOT NULL
        AND edition."Id" = album."TrackListWorkEditionId"
    LIMIT 1
) AS edition ON TRUE
ORDER BY
    root."WorkId" ASC;

-- Service TRACKS: caller binds the exact release, never a filename or a guessed recording.
WITH "Scope" AS MATERIALIZED (
    SELECT
        edition."Id"
    FROM
        "WorkEditions" AS edition
    WHERE
        edition."PublicId" = @EditionPublicId
        AND @PageSize BETWEEN 1 AND 100
        AND @Offset BETWEEN 0 AND 100000
        AND EXISTS (
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
), "PageRoots" AS MATERIALIZED (
    SELECT
        placement."WorkTrackId",
        placement."DiscNumber",
        placement."TrackNumber",
        placement."DurationMs",
        placement."MusicRecordingId"
    FROM
        "Scope" AS scope
    INNER JOIN LATERAL (
        SELECT
            placement."WorkTrackId",
            placement."DiscNumber",
            placement."TrackNumber",
            placement."DurationMs",
            placement."MusicRecordingId"
        FROM
            "MusicTrackPlacements" AS placement
        WHERE
            placement."WorkEditionId" = scope."Id"
        ORDER BY
            placement."DiscNumber" ASC,
            placement."TrackNumber" ASC
        LIMIT @PageSize OFFSET @Offset
    ) AS placement ON TRUE
)
SELECT
    track."PublicId" AS "TrackPublicId",
    track."DisplayName",
    root."DiscNumber",
    root."TrackNumber",
    root."DurationMs",
    recording."PublicId" AS "RecordingPublicId"
FROM
    "PageRoots" AS root
INNER JOIN LATERAL (
    SELECT
        track."PublicId",
        track."DisplayName"
    FROM
        "WorkTracks" AS track
    WHERE
        track."Id" = root."WorkTrackId"
    LIMIT 1
) AS track ON TRUE
LEFT JOIN LATERAL (
    SELECT
        recording."PublicId"
    FROM
        "MusicRecordings" AS recording
    WHERE
        root."MusicRecordingId" IS NOT NULL
        AND recording."Id" = root."MusicRecordingId"
    LIMIT 1
) AS recording ON TRUE
ORDER BY
    root."DiscNumber" ASC,
    root."TrackNumber" ASC;
