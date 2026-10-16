-- Logic BEGIN_ARTIST: hold Account, membership and artist through the complete short transaction.
WITH "Actor" AS MATERIALIZED (
    SELECT
        account."Id"
    FROM
        "Accounts" AS account
    WHERE
        account."Id" = @ActorAccountId
        AND account."IsEnabled" = TRUE
        AND account."AccountRoleTypeId" = 1
    FOR UPDATE OF account
), "Target" AS MATERIALIZED (
    SELECT
        actor."Id" AS "ActorAccountId",
        membership."ProfileId",
        person."PublicId" AS "ArtistPublicId"
    FROM
        "Actor" AS actor
    INNER JOIN
        "AccountProfiles" AS membership ON membership."AccountId" = actor."Id"
            AND membership."ProfileId" = @ActiveProfileId
    INNER JOIN
        "People" AS person ON person."PublicId" = @ArtistPublicId
    INNER JOIN
        "MusicArtists" AS artist ON artist."Id" = person."Id"
    WHERE
        @IsMonitored IS NOT NULL
        AND @OnlyFuture IS NOT NULL
        AND (@IsMonitored OR NOT @OnlyFuture)
    FOR SHARE OF membership
    FOR UPDATE OF artist
)
INSERT INTO "Operations" AS operation (
    "OperationKindKey", "OperationStatusTypeId", "IdempotencyKey", "Input", "CreatedAt", "StartedAt")
SELECT
    'monitoring_artist',
    2,
    'monitoring_artist:' || @CommandPublicId::text,
    jsonb_build_object('actorAccountId', target."ActorAccountId", 'profileId', target."ProfileId",
        'artistPublicId', target."ArtistPublicId", 'isMonitored', @IsMonitored, 'onlyFuture', @OnlyFuture),
    @Now,
    @Now
FROM
    "Target" AS target
ON CONFLICT ("IdempotencyKey") DO NOTHING
RETURNING
    operation."Id";

-- Logic RESET_ARTIST: a new command replaces explicit album-root choices, not track or edition decisions.
WITH "Command" AS MATERIALIZED (
    SELECT
        operation."Id",
        operation."Input",
        artist."Id" AS "MusicArtistId"
    FROM
        "Operations" AS operation
    INNER JOIN
        "People" AS person ON person."PublicId" = (operation."Input" ->> 'artistPublicId')::uuid
    INNER JOIN
        "MusicArtists" AS artist ON artist."Id" = person."Id"
    INNER JOIN
        "Accounts" AS account ON account."Id" = @ActorAccountId
    INNER JOIN
        "AccountProfiles" AS membership ON membership."AccountId" = account."Id"
            AND membership."ProfileId" = @ActiveProfileId
    WHERE
        operation."Id" = @OperationId
        AND operation."OperationKindKey" = 'monitoring_artist'
        AND operation."OperationStatusTypeId" = 2
        AND account."IsEnabled" = TRUE
        AND account."AccountRoleTypeId" = 1
        AND (operation."Input" ->> 'actorAccountId')::bigint = account."Id"
        AND (operation."Input" ->> 'profileId')::bigint = membership."ProfileId"
)
DELETE FROM
    "WorkMonitoringDecisions" AS decision
USING
    "Command" AS command,
    "WorkMusicArtists" AS link
WHERE
    link."MusicArtistId" = command."MusicArtistId"
    AND decision."WorkId" = link."WorkId"
    AND num_nonnulls(decision."WorkSeasonId", decision."WorkEpisodeId", decision."WorkVolumeId",
        decision."WorkChapterId", decision."WorkTrackId", decision."WorkEditionId") = 0;

-- Logic SOURCE_ON: the shared source is the only persisted follow intent.
WITH "Command" AS MATERIALIZED (
    SELECT
        operation."Id",
        operation."Input",
        artist."Id" AS "MusicArtistId"
    FROM
        "Operations" AS operation
    INNER JOIN
        "People" AS person ON person."PublicId" = (operation."Input" ->> 'artistPublicId')::uuid
    INNER JOIN
        "MusicArtists" AS artist ON artist."Id" = person."Id"
    INNER JOIN
        "Accounts" AS account ON account."Id" = @ActorAccountId
    INNER JOIN
        "AccountProfiles" AS membership ON membership."AccountId" = account."Id"
            AND membership."ProfileId" = @ActiveProfileId
    WHERE
        operation."Id" = @OperationId
        AND operation."OperationKindKey" = 'monitoring_artist'
        AND operation."OperationStatusTypeId" = 2
        AND account."IsEnabled" = TRUE
        AND account."AccountRoleTypeId" = 1
        AND (operation."Input" ->> 'actorAccountId')::bigint = account."Id"
        AND (operation."Input" ->> 'profileId')::bigint = membership."ProfileId"
)
INSERT INTO "WorkMonitoringSources" AS source (
    "MonitoringRelationTypeId", "MusicArtistId", "AddedByProfileId", "UpdatedAt")
SELECT
    4,
    command."MusicArtistId",
    @ActiveProfileId,
    @Now
FROM
    "Command" AS command
WHERE
    (command."Input" ->> 'isMonitored')::boolean = TRUE
ON CONFLICT ("MonitoringRelationTypeId", "PersonId", "StudioName", "CollectionId", "MusicArtistId")
DO UPDATE SET
    "UpdatedAt" = EXCLUDED."UpdatedAt"
RETURNING
    source."Id";

-- Logic SOURCE_OFF: explicit album/track intent is handled by the same canonical decisions.
WITH "Command" AS MATERIALIZED (
    SELECT
        operation."Id",
        operation."Input",
        artist."Id" AS "MusicArtistId"
    FROM
        "Operations" AS operation
    INNER JOIN
        "People" AS person ON person."PublicId" = (operation."Input" ->> 'artistPublicId')::uuid
    INNER JOIN
        "MusicArtists" AS artist ON artist."Id" = person."Id"
    INNER JOIN
        "Accounts" AS account ON account."Id" = @ActorAccountId
    INNER JOIN
        "AccountProfiles" AS membership ON membership."AccountId" = account."Id"
            AND membership."ProfileId" = @ActiveProfileId
    WHERE
        operation."Id" = @OperationId
        AND operation."OperationKindKey" = 'monitoring_artist'
        AND operation."OperationStatusTypeId" = 2
        AND account."IsEnabled" = TRUE
        AND account."AccountRoleTypeId" = 1
        AND (operation."Input" ->> 'actorAccountId')::bigint = account."Id"
        AND (operation."Input" ->> 'profileId')::bigint = membership."ProfileId"
)
DELETE FROM
    "WorkMonitoringSources" AS source
USING
    "Command" AS command
WHERE
    source."MusicArtistId" = command."MusicArtistId"
    AND (command."Input" ->> 'isMonitored')::boolean = FALSE;

-- Logic FUTURE_ARTIST: snapshot known album roots; later catalog discovery inherits the source.
WITH "Command" AS MATERIALIZED (
    SELECT
        operation."Id",
        operation."Input",
        artist."Id" AS "MusicArtistId"
    FROM
        "Operations" AS operation
    INNER JOIN
        "People" AS person ON person."PublicId" = (operation."Input" ->> 'artistPublicId')::uuid
    INNER JOIN
        "MusicArtists" AS artist ON artist."Id" = person."Id"
    INNER JOIN
        "Accounts" AS account ON account."Id" = @ActorAccountId
    INNER JOIN
        "AccountProfiles" AS membership ON membership."AccountId" = account."Id"
            AND membership."ProfileId" = @ActiveProfileId
    WHERE
        operation."Id" = @OperationId
        AND operation."OperationKindKey" = 'monitoring_artist'
        AND operation."OperationStatusTypeId" = 2
        AND account."IsEnabled" = TRUE
        AND account."AccountRoleTypeId" = 1
        AND (operation."Input" ->> 'actorAccountId')::bigint = account."Id"
        AND (operation."Input" ->> 'profileId')::bigint = membership."ProfileId"
)
INSERT INTO "WorkMonitoringDecisions" AS decision ("WorkId", "IsMonitored")
SELECT
    link."WorkId",
    FALSE
FROM
    "Command" AS command
INNER JOIN
    "WorkMusicArtists" AS link ON link."MusicArtistId" = command."MusicArtistId"
WHERE
    (command."Input" ->> 'isMonitored')::boolean = TRUE
    AND (command."Input" ->> 'onlyFuture')::boolean = TRUE
ON CONFLICT ("WorkId", "WorkSeasonId", "WorkEpisodeId", "WorkVolumeId", "WorkChapterId", "WorkTrackId", "WorkEditionId")
DO NOTHING;

-- Logic COMPLETE_ARTIST: commit only after every command statement succeeded in the same transaction.
WITH "Command" AS MATERIALIZED (
    SELECT
        operation."Id",
        operation."Input",
        artist."Id" AS "MusicArtistId"
    FROM
        "Operations" AS operation
    INNER JOIN
        "People" AS person ON person."PublicId" = (operation."Input" ->> 'artistPublicId')::uuid
    INNER JOIN
        "MusicArtists" AS artist ON artist."Id" = person."Id"
    INNER JOIN
        "Accounts" AS account ON account."Id" = @ActorAccountId
    INNER JOIN
        "AccountProfiles" AS membership ON membership."AccountId" = account."Id"
            AND membership."ProfileId" = @ActiveProfileId
    WHERE
        operation."Id" = @OperationId
        AND operation."OperationKindKey" = 'monitoring_artist'
        AND operation."OperationStatusTypeId" = 2
        AND account."IsEnabled" = TRUE
        AND account."AccountRoleTypeId" = 1
        AND (operation."Input" ->> 'actorAccountId')::bigint = account."Id"
        AND (operation."Input" ->> 'profileId')::bigint = membership."ProfileId"
)
UPDATE
    "Operations" AS operation
SET
    "OperationStatusTypeId" = 3,
    "FinishedAt" = @Now
FROM
    "Command" AS command
WHERE
    operation."Id" = command."Id"
    AND @Now >= operation."StartedAt"
RETURNING
    operation."PublicId" AS "OperationPublicId";

-- Service RECEIPT: a retry compares the exact typed payload and never repeats Future against newly discovered albums.
SELECT
    operation."PublicId" AS "OperationPublicId"
FROM
    "Operations" AS operation
INNER JOIN
    "Accounts" AS account ON account."Id" = @ActorAccountId
INNER JOIN
    "AccountProfiles" AS membership ON membership."AccountId" = account."Id"
        AND membership."ProfileId" = @ActiveProfileId
WHERE
    account."IsEnabled" = TRUE
    AND account."AccountRoleTypeId" = 1
    AND operation."OperationKindKey" = 'monitoring_artist'
    AND operation."OperationStatusTypeId" = 3
    AND operation."IdempotencyKey" = 'monitoring_artist:' || @CommandPublicId::text
    AND operation."Input" = jsonb_build_object('actorAccountId', account."Id", 'profileId', membership."ProfileId",
        'artistPublicId', @ArtistPublicId, 'isMonitored', @IsMonitored, 'onlyFuture', @OnlyFuture);

-- Service EFFECTIVE: one bounded batch, ordinary decisions override relation reach, absent means inherit.
WITH "Roots" AS MATERIALIZED (
    SELECT
        work."Id",
        work."PublicId"
    FROM
        "Works" AS work
    WHERE
        work."PublicId" = ANY(@WorkPublicIds)
        AND cardinality(@WorkPublicIds) BETWEEN 1 AND 100
        AND array_ndims(@WorkPublicIds) = 1
        AND array_position(@WorkPublicIds, NULL) IS NULL
        AND EXISTS (
            SELECT
                1
            FROM
                "Accounts" AS account
            INNER JOIN
                "AccountProfiles" AS membership ON membership."AccountId" = account."Id"
            WHERE
                account."Id" = @ActorAccountId
                AND account."AccountRoleTypeId" = 1
                AND account."IsEnabled" = TRUE
                AND membership."ProfileId" = @ActiveProfileId
        )
)
SELECT
    root."PublicId" AS "WorkPublicId",
    COALESCE(decision."IsMonitored", reach."IsReached", FALSE) AS "IsWorkMonitored"
FROM
    "Roots" AS root
LEFT JOIN
    "WorkMonitoringDecisions" AS decision ON decision."WorkId" = root."Id"
        AND num_nonnulls(decision."WorkSeasonId", decision."WorkEpisodeId", decision."WorkVolumeId",
            decision."WorkChapterId", decision."WorkTrackId", decision."WorkEditionId") = 0
LEFT JOIN LATERAL (
    SELECT
        EXISTS (
            SELECT
                1
            FROM
                "WorkMusicArtists" AS link
            INNER JOIN
                "WorkMonitoringSources" AS source ON source."MusicArtistId" = link."MusicArtistId"
            WHERE
                link."WorkId" = root."Id"
            UNION ALL
            SELECT
                1
            FROM
                "CollectionWorks" AS link
            INNER JOIN
                "WorkMonitoringSources" AS source ON source."CollectionId" = link."CollectionId"
            WHERE
                link."WorkId" = root."Id"
            UNION ALL
            SELECT
                1
            FROM
                "WorkMetadataFacts" AS facts
            CROSS JOIN LATERAL
                unnest(facts."Studios") AS studio(name)
            INNER JOIN LATERAL (
                SELECT
                    1
                FROM
                    "WorkMonitoringSources" AS source
                WHERE
                    source."StudioName" = lower(studio.name)
                LIMIT 1
            ) AS source ON TRUE
            WHERE
                facts."WorkId" = root."Id"
            UNION ALL
            SELECT
                1
            FROM
                "WorkEditions" AS edition
            INNER JOIN
                "WorkCredits" AS credit ON credit."WorkEditionId" = edition."Id"
            INNER JOIN
                "WorkCreditRoleTypes" AS role ON role."Id" = credit."WorkCreditRoleTypeId"
            INNER JOIN
                "WorkMonitoringSources" AS source ON source."PersonId" = credit."PersonId"
            WHERE
                edition."WorkId" = root."Id"
                AND (source."RoleNames" IS NULL
                    OR role."Key" = ANY(source."RoleNames")
                    OR lower(credit."RoleName") = ANY(source."RoleNames"))
        ) AS "IsReached"
) AS reach ON decision."Id" IS NULL
ORDER BY
    root."Id" ASC;
