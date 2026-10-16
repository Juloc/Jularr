\set ON_ERROR_STOP on
\i /tmp/phase_b_music_albums_prepared.sql
\i /tmp/phase_b_music_tracks_prepared.sql
\i /tmp/phase_b_monitoring_effective_prepared.sql
BEGIN;
SET LOCAL statement_timeout='20s';
SELECT "Id" AS actor_id FROM "Accounts" WHERE "Email"='music-lifecycle-a@example.invalid' \gset
SELECT "Id" AS profile_id FROM "Profiles" WHERE "DisplayName"='Music lifecycle profile' \gset
SELECT "Id" AS other_profile FROM "Profiles" WHERE "DisplayName"='Music lifecycle other profile' \gset
SELECT "Id" AS artist_id,"PublicId" AS artist_public FROM "People" WHERE "DisplayName"='Lifecycle artist' \gset
SELECT "Id" AS work_a,"PublicId" AS public_a FROM "Works" WHERE "CanonicalTitle"='Music lifecycle album A' \gset
SELECT "Id" AS work_b FROM "Works" WHERE "CanonicalTitle"='Music lifecycle album B' \gset
SELECT "Id" AS edition_a,"PublicId" AS edition_public FROM "WorkEditions" WHERE "Name"='Lifecycle release A' \gset
SELECT "Id" AS edition_b FROM "WorkEditions" WHERE "Name"='Lifecycle release B' \gset
SELECT "Id" AS track_a FROM "WorkTracks" WHERE "DisplayName"='Track one' AND "WorkId"=:work_a \gset
SELECT 1 / CASE WHEN NOT EXISTS (
    SELECT
        1
    FROM
        (VALUES (ARRAY[]::text[]), (ARRAY['Writer']), (ARRAY['writer','writer']),
            (ARRAY[NULL]::text[]), (ARRAY[['writer','director']]), (ARRAY[' writer'])) AS invalid(roles)
    WHERE
        "IsMonitoringRoleFilterValid"(invalid.roles)
) THEN 1 ELSE 0 END;
SELECT 1 / CASE WHEN "IsMonitoringRoleFilterValid"(ARRAY['writer','cast']) THEN 1 ELSE 0 END;
DO $placement_constraints$
DECLARE
    work_a bigint;
    work_b bigint;
    edition_a bigint;
    edition_b bigint;
    profile_a bigint;
    profile_b bigint;
    artist_id bigint;
    track_id bigint;
    duplicate_track bigint;
    game_id bigint;
    rejected text;
BEGIN
    SELECT "Id" INTO work_a FROM "Works" WHERE "CanonicalTitle"='Music lifecycle album A';
    SELECT "Id" INTO work_b FROM "Works" WHERE "CanonicalTitle"='Music lifecycle album B';
    SELECT "Id" INTO edition_a FROM "WorkEditions" WHERE "Name"='Lifecycle release A';
    SELECT "Id" INTO edition_b FROM "WorkEditions" WHERE "Name"='Lifecycle release B';
    SELECT "Id" INTO profile_a FROM "Profiles" WHERE "DisplayName"='Music lifecycle profile';
    SELECT "Id" INTO profile_b FROM "Profiles" WHERE "DisplayName"='Music lifecycle other profile';
    SELECT "Id" INTO artist_id FROM "People" WHERE "DisplayName"='Lifecycle artist';
    BEGIN
        INSERT INTO "WorkCredits" ("WorkEditionId", "PersonId", "WorkCreditRoleTypeId", "RoleName")
        VALUES (edition_a,artist_id,2,'Writer');
        RAISE EXCEPTION 'Duplicate edition/person/role accepted';
    EXCEPTION WHEN unique_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'UX_WorkCredits_Role' THEN RAISE; END IF;
    END;
    SELECT "Id" INTO track_id FROM "WorkTracks" WHERE "WorkId"=work_a AND "OrderIndex"=0;
    INSERT INTO "WorkCredits" ("WorkEditionId", "PersonId", "WorkCreditRoleTypeId") VALUES (edition_a,artist_id,3);
    BEGIN
        INSERT INTO "WorkCredits" ("WorkEditionId", "PersonId", "WorkCreditRoleTypeId") VALUES (edition_a,artist_id,3);
        RAISE EXCEPTION 'Duplicate NULL role accepted';
    EXCEPTION WHEN unique_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'UX_WorkCredits_Role' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "MusicTrackPlacements" SET "WorkId"=work_b WHERE "WorkTrackId"=track_id;
        RAISE EXCEPTION 'Placement accepted another track Work';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'FK_MusicTrackPlacements_WorkTracks' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "MusicTrackPlacements" SET "WorkEditionId"=edition_b WHERE "WorkTrackId"=track_id;
        RAISE EXCEPTION 'Placement accepted another Work release';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'FK_MusicTrackPlacements_Editions' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "MusicAlbums" SET "TrackListWorkEditionId"=edition_b WHERE "WorkId"=work_a;
        RAISE EXCEPTION 'Selected track list accepted another Work';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'FK_MusicAlbums_TrackListEdition' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "MusicAlbums" SET "MusicAlbumTypeId"=254 WHERE "WorkId"=work_a;
        RAISE EXCEPTION 'Unknown album type accepted';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'FK_MusicAlbums_Types' THEN RAISE; END IF;
    END;
    INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (7,'Not a music album') RETURNING "Id" INTO game_id;
    BEGIN
        INSERT INTO "MusicAlbums" ("WorkId", "MusicAlbumTypeId") VALUES (game_id,1);
        RAISE EXCEPTION 'Game accepted music album extension';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'FK_MusicAlbums_MusicWorks' THEN RAISE; END IF;
    END;
    INSERT INTO "WorkTracks" ("WorkId", "OrderIndex", "DisplayName") VALUES (work_a,3,'Duplicate disc placement')
    RETURNING "Id" INTO duplicate_track;
    BEGIN
        INSERT INTO "MusicTrackPlacements" ("WorkTrackId", "WorkId", "WorkEditionId", "DiscNumber", "TrackNumber")
        VALUES (duplicate_track,work_a,edition_a,1,1);
        RAISE EXCEPTION 'Duplicate release/disc/track accepted';
    EXCEPTION WHEN unique_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'UX_MusicTrackPlacements_Edition_Disc_Track' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "MusicTrackPlacements" SET "DiscNumber"=0 WHERE "WorkTrackId"=track_id;
        RAISE EXCEPTION 'Nonpositive disc accepted';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'CK_MusicTrackPlacements_Numbers' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "MusicTrackPlacements" SET "DurationMs"=-1 WHERE "WorkTrackId"=track_id;
        RAISE EXCEPTION 'Negative track duration accepted';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'CK_MusicTrackPlacements_Duration' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "WorkMonitoringSources" SET "AddedByProfileId"=profile_b WHERE "CollectionId" IS NOT NULL;
        RAISE EXCEPTION 'Private collection monitoring crossed profile scope';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'FK_WorkMonitoringSources_Collections' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO "WorkMonitoringSources" ("MonitoringRelationTypeId", "MusicArtistId", "AddedByProfileId")
        VALUES (1,artist_id,profile_a);
        RAISE EXCEPTION 'Source type accepted wrong target field';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'CK_WorkMonitoringSources_Target' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO "WorkMonitoringSources" ("MonitoringRelationTypeId", "MusicArtistId", "RoleNames", "AddedByProfileId")
        VALUES (4,artist_id,ARRAY['writer'],profile_a);
        RAISE EXCEPTION 'Artist accepted Person-only role filter';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
        IF rejected <> 'CK_WorkMonitoringSources_Roles' THEN RAISE; END IF;
    END;
END $placement_constraints$;

WITH added AS (
    INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle")
    SELECT
        6,
        'Paged lifecycle album ' || fixture.index
    FROM
        generate_series(1,1000) AS fixture(index)
    RETURNING "Id"
), albums AS (
    INSERT INTO "MusicAlbums" ("WorkId", "MusicAlbumTypeId")
    SELECT
        added."Id",
        1
    FROM added
    RETURNING "WorkId"
)
INSERT INTO "WorkMusicArtists" ("WorkId", "MusicArtistId") SELECT albums."WorkId",:artist_id FROM albums;
WITH added AS (
    INSERT INTO "WorkTracks" ("WorkId", "OrderIndex", "DisplayName")
    SELECT
        :work_a,
        10 + fixture.index,
        'Paged lifecycle track ' || fixture.index
    FROM
        generate_series(1,1000) AS fixture(index)
    RETURNING "Id","OrderIndex"
)
INSERT INTO "MusicTrackPlacements" ("WorkTrackId", "WorkId", "WorkEditionId", "DiscNumber", "TrackNumber")
SELECT
    added."Id",
    :work_a,
    :edition_a,
    3,
    added."OrderIndex" - 10
FROM added;
INSERT INTO "WorkMonitoringSources" ("MonitoringRelationTypeId", "StudioName", "AddedByProfileId")
SELECT
    2,
    'unused studio ' || fixture.index,
    :profile_id
FROM
    generate_series(1,1000) AS fixture(index);
EXECUTE phase_b_music_albums(:actor_id,:profile_id,:'artist_public',25,975);
SELECT 1 / CASE WHEN :ROW_COUNT=25 THEN 1 ELSE 0 END;
EXECUTE phase_b_music_albums(:actor_id,:profile_id,:'artist_public',25,1000);
SELECT 1 / CASE WHEN :ROW_COUNT=3 THEN 1 ELSE 0 END;
EXECUTE phase_b_music_tracks(:actor_id,:profile_id,:'edition_public',25,975);
SELECT 1 / CASE WHEN :ROW_COUNT=25 THEN 1 ELSE 0 END;
EXECUTE phase_b_music_tracks(:actor_id,:profile_id,:'edition_public',25,1000);
SELECT 1 / CASE WHEN :ROW_COUNT=2 THEN 1 ELSE 0 END;
EXECUTE phase_b_music_albums(:actor_id,:profile_id,gen_random_uuid(),25,0);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_music_tracks(:actor_id,:profile_id,:'edition_public',101,0);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_monitoring_effective(:actor_id,:profile_id,ARRAY[]::uuid[]);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_monitoring_effective(:actor_id,:profile_id,array_fill(:'public_a'::uuid,ARRAY[101]));
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
ANALYZE "WorkMusicArtists";
ANALYZE "MusicAlbums";
ANALYZE "Works";
ANALYZE "WorkTracks";
ANALYZE "MusicTrackPlacements";
ANALYZE "WorkMonitoringSources";
EXPLAIN (ANALYZE,BUFFERS) EXECUTE phase_b_music_albums(:actor_id,:profile_id,:'artist_public',25,0);
EXPLAIN (ANALYZE,BUFFERS) EXECUTE phase_b_music_tracks(:actor_id,:profile_id,:'edition_public',25,0);
EXPLAIN (ANALYZE,BUFFERS) EXECUTE phase_b_monitoring_effective(:actor_id,:profile_id,ARRAY[:'public_a'::uuid]);
ROLLBACK;
