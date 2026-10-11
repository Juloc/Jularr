\set ON_ERROR_STOP on
\i /tmp/phase_b_music_artists_prepared.sql
\i /tmp/phase_b_music_artist_prepared.sql
\i /tmp/phase_b_music_recordings_prepared.sql
BEGIN;
SET LOCAL statement_timeout = '20s';
INSERT INTO "UiLocales" ("Locale", "Name") VALUES ('music-identity-test', 'Music') RETURNING "Id" AS locale_id \gset
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('music-identity-a@example.invalid', 'Music A', 1) RETURNING "Id" AS actor_id \gset
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('music-identity-b@example.invalid', 'Music B', 2) RETURNING "Id" AS other_id \gset
INSERT INTO "Profiles" ("OwnerAccountId", "DisplayName", "UiLocaleId")
VALUES (:actor_id, 'Music identity profile', :locale_id) RETURNING "Id" AS profile_id \gset
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (:actor_id, :profile_id);
INSERT INTO "Providers" ("Key") VALUES ('music-identity-source') RETURNING "Id" AS provider_id \gset
INSERT INTO "Providers" ("Key") VALUES ('music-identity-other-source') RETURNING "Id" AS other_provider_id \gset
INSERT INTO "People" ("DisplayName", "SortName") VALUES ('Artist''s Ensemble', 'Identity artist')
RETURNING "Id" AS artist_id, "PublicId" AS artist_public \gset
INSERT INTO "MusicArtists" ("Id", "AddedByProfileId") VALUES (:artist_id, :profile_id);
INSERT INTO "People" ("DisplayName", "SortName") VALUES ('Artist''s Ensemble', 'Identity artist')
RETURNING "Id" AS other_artist_id, "PublicId" AS other_artist_public \gset
INSERT INTO "MusicArtists" ("Id") VALUES (:other_artist_id);
INSERT INTO "PersonExternalIdentities" ("PersonId", "ProviderId", "ProviderNamespace", "ExternalId")
VALUES (:artist_id, :provider_id, 'artist', 'artist-quoted''; --'), (:other_artist_id, :other_provider_id, 'artist', 'artist-quoted''; --');
INSERT INTO "MusicRecordings" ("DisplayName", "DurationMs") VALUES ('Recording A', 123000)
RETURNING "Id" AS recording_id, "PublicId" AS recording_public \gset
INSERT INTO "MusicRecordingExternalIdentities" ("MusicRecordingId", "ProviderId", "ProviderNamespace", "ExternalId")
VALUES (:recording_id, :provider_id, 'recording', 'shared-recording');
INSERT INTO "MusicRecordings" ("DisplayName") VALUES ('Recording B') RETURNING "Id" AS other_recording_id \gset
INSERT INTO "MusicRecordingExternalIdentities" ("MusicRecordingId", "ProviderId", "ProviderNamespace", "ExternalId")
VALUES (:other_recording_id, :other_provider_id, 'recording', 'shared-recording');
INSERT INTO "MusicRecordingArtists" ("MusicRecordingId", "MusicArtistId", "CreditName")
VALUES (:recording_id, :artist_id, 'Credited name');
INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (6, 'Music identity album A') RETURNING "Id" AS work_a \gset
INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (6, 'Music identity album B') RETURNING "Id" AS work_b \gset
INSERT INTO "WorkMusicArtists" ("WorkId", "MusicArtistId") VALUES (:work_a, :artist_id), (:work_b, :artist_id);
INSERT INTO "WorkTracks" ("WorkId", "OrderIndex", "DisplayName") VALUES (:work_a, 0, 'Placement A') RETURNING "Id" AS track_a \gset
INSERT INTO "WorkTracks" ("WorkId", "OrderIndex", "DisplayName") VALUES (:work_b, 0, 'Placement B') RETURNING "Id" AS track_b \gset
INSERT INTO "WorkTrackRecordings" ("WorkTrackId", "MusicRecordingId") VALUES (:track_a, :recording_id), (:track_b, :recording_id);
INSERT INTO "WorkEditions" ("WorkId", "Name") VALUES (:work_a, 'Music credits') RETURNING "Id" AS edition_id \gset
INSERT INTO "WorkCredits" ("WorkEditionId", "PersonId", "WorkCreditRoleTypeId") VALUES (:edition_id, :artist_id, 2);
SELECT 1 / CASE WHEN (SELECT count(*) FROM "WorkTrackRecordings" WHERE "MusicRecordingId" = :recording_id) = 2 THEN 1 ELSE 0 END;
EXECUTE phase_b_music_artist(:actor_id, :profile_id, :'artist_public') \gset
SELECT 1 / CASE WHEN :'ArtistPublicId'::uuid = :'artist_public'::uuid AND :'DisplayName' = 'Artist''s Ensemble' THEN 1 ELSE 0 END;
EXECUTE phase_b_music_recordings(:actor_id, :profile_id, :'artist_public', 25, 0) \gset
SELECT 1 / CASE WHEN :'RecordingPublicId'::uuid = :'recording_public'::uuid AND :DurationMs = 123000 AND :'CreditName' = 'Credited name' THEN 1 ELSE 0 END;
EXECUTE phase_b_music_recordings(:actor_id, :profile_id, :'other_artist_public', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
UPDATE "People" SET "DisplayName" = 'Renamed once' WHERE "Id" = :artist_id;
EXECUTE phase_b_music_artist(:actor_id, :profile_id, :'artist_public') \gset
SELECT 1 / CASE WHEN :'DisplayName' = 'Renamed once' THEN 1 ELSE 0 END;

WITH added AS (
    INSERT INTO "People" ("DisplayName", "SortName")
    SELECT
        'Music artist ' || fixture.index,
        'Paging music'
    FROM
        generate_series(1, 1000) AS fixture(index)
    RETURNING "Id"
)
INSERT INTO "MusicArtists" ("Id") SELECT added."Id" FROM added;
WITH added AS (
    INSERT INTO "MusicRecordings" ("DisplayName")
    SELECT
        'Music recording ' || fixture.index
    FROM
        generate_series(1, 999) AS fixture(index)
    RETURNING "Id"
)
INSERT INTO "MusicRecordingArtists" ("MusicRecordingId", "MusicArtistId") SELECT added."Id", :artist_id FROM added;
EXECUTE phase_b_music_artists(:actor_id, :profile_id, 25, 975);
SELECT 1 / CASE WHEN :ROW_COUNT = 25 THEN 1 ELSE 0 END;
EXECUTE phase_b_music_artists(:actor_id, :profile_id, 25, 1000);
SELECT 1 / CASE WHEN :ROW_COUNT = 2 THEN 1 ELSE 0 END;
EXECUTE phase_b_music_artists(:actor_id, :profile_id, 1, 0) \gset
SELECT 1 / CASE WHEN :'ArtistPublicId'::uuid = :'artist_public'::uuid THEN 1 ELSE 0 END;
EXECUTE phase_b_music_artists(:actor_id, :profile_id, 1, 1) \gset
SELECT 1 / CASE WHEN :'ArtistPublicId'::uuid = :'other_artist_public'::uuid THEN 1 ELSE 0 END;
EXECUTE phase_b_music_recordings(:actor_id, :profile_id, :'artist_public', 25, 975);
SELECT 1 / CASE WHEN :ROW_COUNT = 25 THEN 1 ELSE 0 END;
EXECUTE phase_b_music_recordings(:actor_id, :profile_id, :'artist_public', 25, 1000);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_music_artists(:actor_id, :profile_id, 101, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
SAVEPOINT invalid_offset;
\set ON_ERROR_STOP off
EXECUTE phase_b_music_artists(:actor_id, :profile_id, 25, -1);
\set invalid_offset_state :SQLSTATE
\set ON_ERROR_STOP on
ROLLBACK TO SAVEPOINT invalid_offset;
SELECT 1 / CASE WHEN :'invalid_offset_state' = '2201X' THEN 1 ELSE 0 END;
EXECUTE phase_b_music_recordings(:actor_id, :profile_id, :'artist_public', 25, 100001);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_music_recordings(:actor_id, :profile_id, gen_random_uuid(), 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_music_artist(:other_id, :profile_id, :'artist_public');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_music_artists(:other_id, :profile_id, 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_music_recordings(:other_id, :profile_id, :'artist_public', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (:other_id, :profile_id);
EXECUTE phase_b_music_artist(:other_id, :profile_id, :'artist_public');
SELECT 1 / CASE WHEN :ROW_COUNT = 1 THEN 1 ELSE 0 END;
DELETE FROM "AccountProfiles" WHERE "AccountId" = :other_id AND "ProfileId" = :profile_id;
EXECUTE phase_b_music_artist(:other_id, :profile_id, :'artist_public');
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
UPDATE "Accounts" SET "IsEnabled" = FALSE WHERE "Id" = :actor_id;
EXECUTE phase_b_music_artists(:actor_id, :profile_id, 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
EXECUTE phase_b_music_recordings(:actor_id, :profile_id, :'artist_public', 25, 0);
SELECT 1 / CASE WHEN :ROW_COUNT = 0 THEN 1 ELSE 0 END;
UPDATE "Accounts" SET "IsEnabled" = TRUE WHERE "Id" = :actor_id;
ANALYZE "People";
ANALYZE "MusicArtists";
ANALYZE "MusicRecordings";
ANALYZE "MusicRecordingArtists";
EXPLAIN (ANALYZE, BUFFERS) EXECUTE phase_b_music_artists(:actor_id, :profile_id, 25, 0);
EXPLAIN (ANALYZE, BUFFERS) EXECUTE phase_b_music_recordings(:actor_id, :profile_id, :'artist_public', 25, 0);
DO $music_identity$
DECLARE
    artist_id bigint;
    other_artist_id bigint;
    recording_id bigint;
    other_recording_id bigint;
    provider_id bigint;
    rejected text;
BEGIN
    SELECT "Id" INTO artist_id FROM "People" WHERE "DisplayName" = 'Renamed once';
    SELECT "Id" INTO other_artist_id FROM "People" WHERE "DisplayName" = 'Artist''s Ensemble';
    SELECT "Id" INTO recording_id FROM "MusicRecordings" WHERE "DisplayName" = 'Recording A';
    SELECT "Id" INTO other_recording_id FROM "MusicRecordings" WHERE "DisplayName" = 'Recording B';
    SELECT "Id" INTO provider_id FROM "Providers" WHERE "Key" = 'music-identity-source';
    BEGIN
        INSERT INTO "MusicArtists" ("Id") VALUES (-1);
        RAISE EXCEPTION 'Artist accepted unknown credited identity';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected = CONSTRAINT_NAME;
        IF rejected <> 'FK_MusicArtists_People' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO "MusicArtists" ("Id") VALUES (artist_id);
        RAISE EXCEPTION 'Second artist root accepted';
    EXCEPTION WHEN unique_violation THEN
        GET STACKED DIAGNOSTICS rejected = CONSTRAINT_NAME;
        IF rejected <> 'PK_MusicArtists' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO "PersonExternalIdentities" ("PersonId", "ProviderId", "ProviderNamespace", "ExternalId")
        VALUES (other_artist_id, provider_id, 'artist', 'artist-quoted''; --');
        RAISE EXCEPTION 'Provider artist identity reused';
    EXCEPTION WHEN unique_violation THEN
        GET STACKED DIAGNOSTICS rejected = CONSTRAINT_NAME;
        IF rejected <> 'UX_PersonExternalIdentities_ProviderIdentity' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO "MusicRecordingExternalIdentities" ("MusicRecordingId", "ProviderId", "ProviderNamespace", "ExternalId")
        VALUES (other_recording_id, provider_id, 'recording', 'shared-recording');
        RAISE EXCEPTION 'Provider recording identity reused across recordings';
    EXCEPTION WHEN unique_violation THEN
        GET STACKED DIAGNOSTICS rejected = CONSTRAINT_NAME;
        IF rejected <> 'UX_MusicRecordingExternalIdentities_ProviderIdentity' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO "MusicRecordingExternalIdentities" ("MusicRecordingId", "ProviderId", "ProviderNamespace", "ExternalId")
        VALUES (-1, provider_id, 'recording', 'missing-recording');
        RAISE EXCEPTION 'External identity accepted unknown recording';
    EXCEPTION WHEN foreign_key_violation THEN
        GET STACKED DIAGNOSTICS rejected = CONSTRAINT_NAME;
        IF rejected <> 'FK_MusicRecordingExternalIdentities_Recordings' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO "MusicRecordingExternalIdentities" ("MusicRecordingId", "ProviderId", "ProviderNamespace", "ExternalId")
        VALUES (recording_id, provider_id, 'recording', 'another-recording');
        RAISE EXCEPTION 'Second provider identity silently merged';
    EXCEPTION WHEN unique_violation THEN
        GET STACKED DIAGNOSTICS rejected = CONSTRAINT_NAME;
        IF rejected <> 'UX_MusicRecordingExternalIdentities_RecordingProvider' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO "PersonExternalIdentities" ("PersonId", "ProviderId", "ProviderNamespace", "ExternalId")
        VALUES (other_artist_id, provider_id, 'Artist', 'new-artist');
        RAISE EXCEPTION 'Unnormalized provider namespace accepted';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected = CONSTRAINT_NAME;
        IF rejected <> 'CK_PersonExternalIdentities_Namespace' THEN RAISE; END IF;
    END;
    BEGIN
        UPDATE "MusicRecordingArtists" SET "CreditOrder" = -1 WHERE "MusicArtistId" = artist_id;
        RAISE EXCEPTION 'Negative credit order accepted';
    EXCEPTION WHEN check_violation THEN
        GET STACKED DIAGNOSTICS rejected = CONSTRAINT_NAME;
        IF rejected <> 'CK_MusicRecordingArtists_Credit' THEN RAISE; END IF;
    END;
    BEGIN
        DELETE FROM "MusicRecordingExternalIdentities" WHERE "MusicRecordingId" = recording_id;
        DELETE FROM "MusicRecordingArtists" WHERE "MusicRecordingId" = recording_id;
        DELETE FROM "MusicRecordings" WHERE "Id" = recording_id;
        RAISE EXCEPTION 'Recording with placements deleted';
    EXCEPTION WHEN foreign_key_violation OR restrict_violation THEN
        GET STACKED DIAGNOSTICS rejected = CONSTRAINT_NAME;
        IF rejected <> 'FK_WorkTrackRecordings_MusicRecordings' THEN RAISE; END IF;
    END;
END $music_identity$;
SAVEPOINT music_rollback;
UPDATE "People" SET "DisplayName" = 'Rolled back' WHERE "Id" = :artist_id;
ROLLBACK TO SAVEPOINT music_rollback;
EXECUTE phase_b_music_artist(:actor_id, :profile_id, :'artist_public') \gset
SELECT 1 / CASE WHEN :'DisplayName' = 'Renamed once' THEN 1 ELSE 0 END;
SET CONSTRAINTS ALL IMMEDIATE;
ROLLBACK;
