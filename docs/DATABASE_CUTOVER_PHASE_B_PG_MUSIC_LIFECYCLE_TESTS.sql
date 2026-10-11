\set ON_ERROR_STOP on
\i /tmp/phase_b_music_albums_prepared.sql
\i /tmp/phase_b_music_tracks_prepared.sql
\i /tmp/phase_b_monitoring_begin_artist_prepared.sql
\i /tmp/phase_b_monitoring_reset_artist_prepared.sql
\i /tmp/phase_b_monitoring_source_on_prepared.sql
\i /tmp/phase_b_monitoring_source_off_prepared.sql
\i /tmp/phase_b_monitoring_future_artist_prepared.sql
\i /tmp/phase_b_monitoring_complete_artist_prepared.sql
\i /tmp/phase_b_monitoring_receipt_prepared.sql
\i /tmp/phase_b_monitoring_effective_prepared.sql
BEGIN;
SET LOCAL statement_timeout='20s';
INSERT INTO "UiLocales" ("Locale", "Name") VALUES ('music-lifecycle-test','Music lifecycle') RETURNING "Id" AS locale_id \gset
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('music-lifecycle-a@example.invalid','Music lifecycle A',1) RETURNING "Id" AS actor_id \gset
INSERT INTO "Accounts" ("Email", "DisplayName", "AccountRoleTypeId")
VALUES ('music-lifecycle-b@example.invalid','Music lifecycle B',2) RETURNING "Id" AS other_id \gset
INSERT INTO "Profiles" ("OwnerAccountId", "DisplayName", "UiLocaleId")
VALUES (:actor_id,'Music lifecycle profile',:locale_id) RETURNING "Id" AS profile_id \gset
INSERT INTO "Profiles" ("OwnerAccountId", "DisplayName", "UiLocaleId")
VALUES (:other_id,'Music lifecycle other profile',:locale_id) RETURNING "Id" AS other_profile \gset
INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (:actor_id,:profile_id),(:other_id,:other_profile);
INSERT INTO "People" ("DisplayName", "SortName") VALUES ('Lifecycle artist','Lifecycle artist')
RETURNING "Id" AS artist_id,"PublicId" AS artist_public \gset
INSERT INTO "MusicArtists" ("Id", "AddedByProfileId") VALUES (:artist_id,:profile_id);
INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (6,'Music lifecycle album A')
RETURNING "Id" AS work_a,"PublicId" AS public_a \gset
INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (6,'Music lifecycle album B')
RETURNING "Id" AS work_b,"PublicId" AS public_b \gset
INSERT INTO "MusicAlbums" ("WorkId", "MusicAlbumTypeId") VALUES (:work_a,1),(:work_b,2);
INSERT INTO "WorkMusicArtists" ("WorkId", "MusicArtistId") VALUES (:work_a,:artist_id),(:work_b,:artist_id);
INSERT INTO "WorkMetadataFacts" ("WorkId", "FirstPublishedOn", "Studios") VALUES (:work_a,DATE '2020-03-04',ARRAY['A STUDIO']);
INSERT INTO "WorkEditions" ("WorkId", "Name") VALUES (:work_a,'Lifecycle release A')
RETURNING "Id" AS edition_a,"PublicId" AS edition_a_public \gset
INSERT INTO "WorkEditions" ("WorkId", "Name") VALUES (:work_a,'Lifecycle alternate release A')
RETURNING "Id" AS alternate_edition,"PublicId" AS alternate_public \gset
INSERT INTO "WorkEditions" ("WorkId", "Name") VALUES (:work_b,'Lifecycle release B')
RETURNING "Id" AS edition_b \gset
UPDATE "MusicAlbums" SET "TrackListWorkEditionId"=:edition_a WHERE "WorkId"=:work_a;
INSERT INTO "Providers" ("Key") VALUES ('music-lifecycle-source') RETURNING "Id" AS provider_id \gset
INSERT INTO "WorkExternalIdentities" ("WorkId", "ProviderId", "ProviderNamespace", "ExternalId")
VALUES (:work_a,:provider_id,'release_group','group-a');
INSERT INTO "EditionExternalIdentities" ("WorkEditionId", "ProviderId", "ProviderNamespace", "ExternalId")
VALUES (:edition_a,:provider_id,'release','release-a');
INSERT INTO "WorkTracks" ("WorkId", "OrderIndex", "DisplayName") VALUES (:work_a,0,'Track one')
RETURNING "Id" AS track_a,"PublicId" AS track_a_public \gset
INSERT INTO "WorkTracks" ("WorkId", "OrderIndex", "DisplayName") VALUES (:work_a,1,'Disc two track one')
RETURNING "Id" AS track_disc_two \gset
INSERT INTO "WorkTracks" ("WorkId", "OrderIndex", "DisplayName") VALUES (:work_a,2,'Alternate track one')
RETURNING "Id" AS alternate_track \gset
INSERT INTO "MusicRecordings" ("DisplayName") VALUES ('Lifecycle recording')
RETURNING "Id" AS recording_id,"PublicId" AS recording_public \gset
INSERT INTO "MusicTrackPlacements" ("WorkTrackId", "WorkId", "WorkEditionId", "DiscNumber", "TrackNumber", "DurationMs", "MusicRecordingId")
VALUES (:track_a,:work_a,:edition_a,1,1,123000,:recording_id),(:track_disc_two,:work_a,:edition_a,2,1,NULL,NULL),
    (:alternate_track,:work_a,:alternate_edition,1,1,120000,:recording_id);
EXECUTE phase_b_music_albums(:actor_id,:profile_id,:'artist_public',1,0) \gset
SELECT 1 / CASE WHEN :'WorkPublicId'::uuid=:'public_a'::uuid AND :MusicAlbumTypeId=1
    AND :'FirstPublishedOn'::date=DATE '2020-03-04' AND :'TrackListEditionPublicId'::uuid=:'edition_a_public'::uuid THEN 1 ELSE 0 END;
EXECUTE phase_b_music_tracks(:actor_id,:profile_id,:'edition_a_public',1,0) \gset
SELECT 1 / CASE WHEN :'TrackPublicId'::uuid=:'track_a_public'::uuid AND :DiscNumber=1 AND :TrackNumber=1
    AND :DurationMs=123000 AND :'RecordingPublicId'::uuid=:'recording_public'::uuid THEN 1 ELSE 0 END;
EXECUTE phase_b_music_tracks(:actor_id,:profile_id,:'edition_a_public',1,1) \gset
SELECT 1 / CASE WHEN :DiscNumber=2 AND :TrackNumber=1 AND :{?RecordingPublicId}=FALSE THEN 1 ELSE 0 END;
EXECUTE phase_b_music_tracks(:actor_id,:profile_id,:'alternate_public',25,0);
SELECT 1 / CASE WHEN :ROW_COUNT=1 THEN 1 ELSE 0 END;
INSERT INTO "WorkMonitoringDecisions" ("WorkId", "IsMonitored") VALUES (:work_a,TRUE);
INSERT INTO "WorkMonitoringDecisions" ("WorkId", "WorkTrackId", "IsMonitored") VALUES (:work_a,:track_a,TRUE);
EXECUTE phase_b_monitoring_begin_artist(:other_id,:other_profile,:'artist_public',gen_random_uuid(),TRUE,TRUE,now());
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_monitoring_begin_artist(:actor_id,:other_profile,:'artist_public',gen_random_uuid(),TRUE,TRUE,now());
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_monitoring_begin_artist(:actor_id,:profile_id,:'artist_public',gen_random_uuid(),FALSE,TRUE,now());
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
SELECT gen_random_uuid() AS future_command \gset
EXECUTE phase_b_monitoring_begin_artist(:actor_id,:profile_id,:'artist_public',:'future_command',TRUE,TRUE,now()) \gset
SELECT :Id AS command_operation \gset
EXECUTE phase_b_monitoring_reset_artist(:actor_id,:profile_id,:command_operation);
EXECUTE phase_b_monitoring_source_on(:actor_id,:profile_id,:command_operation,now());
EXECUTE phase_b_monitoring_source_off(:actor_id,:profile_id,:command_operation);
EXECUTE phase_b_monitoring_future_artist(:actor_id,:profile_id,:command_operation);
EXECUTE phase_b_monitoring_complete_artist(:actor_id,:profile_id,:command_operation,now());
SELECT 1 / CASE WHEN :ROW_COUNT=1 THEN 1 ELSE 0 END;
EXECUTE phase_b_monitoring_effective(:actor_id,:profile_id,ARRAY[:'public_a'::uuid]) \gset
SELECT 1 / CASE WHEN :'IsWorkMonitored'::boolean=FALSE THEN 1 ELSE 0 END;
SELECT 1 / CASE WHEN EXISTS (SELECT 1 FROM "WorkMonitoringDecisions" WHERE "WorkTrackId"=:track_a AND "IsMonitored") THEN 1 ELSE 0 END;

INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (6,'Music lifecycle later album')
RETURNING "Id" AS later_work,"PublicId" AS later_public \gset
INSERT INTO "MusicAlbums" ("WorkId", "MusicAlbumTypeId") VALUES (:later_work,1);
INSERT INTO "WorkMusicArtists" ("WorkId", "MusicArtistId") VALUES (:later_work,:artist_id);
EXECUTE phase_b_monitoring_begin_artist(:actor_id,:profile_id,:'artist_public',:'future_command',TRUE,TRUE,now());
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_monitoring_receipt(:actor_id,:profile_id,:'artist_public',:'future_command',TRUE,TRUE);
SELECT 1 / CASE WHEN :ROW_COUNT=1 THEN 1 ELSE 0 END;
EXECUTE phase_b_monitoring_receipt(:actor_id,:profile_id,:'artist_public',:'future_command',TRUE,FALSE);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_monitoring_receipt(:other_id,:other_profile,:'artist_public',:'future_command',TRUE,TRUE);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_monitoring_effective(:actor_id,:profile_id,ARRAY[:'later_public'::uuid]) \gset
SELECT 1 / CASE WHEN :'IsWorkMonitored'::boolean=TRUE THEN 1 ELSE 0 END;
SELECT gen_random_uuid() AS all_command \gset
EXECUTE phase_b_monitoring_begin_artist(:actor_id,:profile_id,:'artist_public',:'all_command',TRUE,FALSE,now()) \gset
SELECT :Id AS command_operation \gset
EXECUTE phase_b_monitoring_reset_artist(:actor_id,:profile_id,:command_operation);
EXECUTE phase_b_monitoring_source_on(:actor_id,:profile_id,:command_operation,now());
EXECUTE phase_b_monitoring_source_off(:actor_id,:profile_id,:command_operation);
EXECUTE phase_b_monitoring_future_artist(:actor_id,:profile_id,:command_operation);
EXECUTE phase_b_monitoring_complete_artist(:actor_id,:profile_id,:command_operation,now());
SELECT 1 / CASE WHEN :ROW_COUNT=1 THEN 1 ELSE 0 END;
EXECUTE phase_b_monitoring_effective(:actor_id,:profile_id,ARRAY[:'public_a'::uuid]) \gset
SELECT 1 / CASE WHEN :'IsWorkMonitored'::boolean=TRUE THEN 1 ELSE 0 END;
SELECT gen_random_uuid() AS none_command \gset
EXECUTE phase_b_monitoring_begin_artist(:actor_id,:profile_id,:'artist_public',:'none_command',FALSE,FALSE,now()) \gset
SELECT :Id AS command_operation \gset
EXECUTE phase_b_monitoring_reset_artist(:actor_id,:profile_id,:command_operation);
EXECUTE phase_b_monitoring_source_on(:actor_id,:profile_id,:command_operation,now());
EXECUTE phase_b_monitoring_source_off(:actor_id,:profile_id,:command_operation);
EXECUTE phase_b_monitoring_future_artist(:actor_id,:profile_id,:command_operation);
EXECUTE phase_b_monitoring_complete_artist(:actor_id,:profile_id,:command_operation,now());
SELECT 1 / CASE WHEN :ROW_COUNT=1 THEN 1 ELSE 0 END;
EXECUTE phase_b_monitoring_effective(:actor_id,:profile_id,ARRAY[:'public_a'::uuid]) \gset
SELECT 1 / CASE WHEN :'IsWorkMonitored'::boolean=FALSE THEN 1 ELSE 0 END;
SAVEPOINT command_rollback;
SELECT gen_random_uuid() AS rollback_command \gset
EXECUTE phase_b_monitoring_begin_artist(:actor_id,:profile_id,:'artist_public',:'rollback_command',TRUE,FALSE,now()) \gset
SELECT :Id AS command_operation \gset
EXECUTE phase_b_monitoring_reset_artist(:actor_id,:profile_id,:command_operation);
EXECUTE phase_b_monitoring_source_on(:actor_id,:profile_id,:command_operation,now());
EXECUTE phase_b_monitoring_source_off(:actor_id,:profile_id,:command_operation);
EXECUTE phase_b_monitoring_future_artist(:actor_id,:profile_id,:command_operation);
EXECUTE phase_b_monitoring_complete_artist(:actor_id,:profile_id,:command_operation,now());
SELECT 1 / CASE WHEN :ROW_COUNT=1 THEN 1 ELSE 0 END;
ROLLBACK TO SAVEPOINT command_rollback;
EXECUTE phase_b_monitoring_receipt(:actor_id,:profile_id,:'artist_public',:'rollback_command',TRUE,FALSE);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
SELECT 1 / CASE WHEN NOT EXISTS (SELECT 1 FROM "WorkMonitoringSources" WHERE "MusicArtistId"=:artist_id) THEN 1 ELSE 0 END;
INSERT INTO "WorkCredits" ("WorkEditionId", "PersonId", "WorkCreditRoleTypeId", "RoleName")
VALUES (:edition_a,:artist_id,2,'Writer');
INSERT INTO "WorkMonitoringSources" ("MonitoringRelationTypeId", "PersonId", "RoleNames", "AddedByProfileId")
VALUES (1,:artist_id,ARRAY['director'],:profile_id) RETURNING "Id" AS person_source \gset
EXECUTE phase_b_monitoring_effective(:actor_id,:profile_id,ARRAY[:'public_a'::uuid]) \gset
SELECT 1 / CASE WHEN :'IsWorkMonitored'::boolean=FALSE THEN 1 ELSE 0 END;
UPDATE "WorkMonitoringSources" SET "RoleNames"=ARRAY['writer'] WHERE "Id"=:person_source;
EXECUTE phase_b_monitoring_effective(:actor_id,:profile_id,ARRAY[:'public_a'::uuid]) \gset
SELECT 1 / CASE WHEN :'IsWorkMonitored'::boolean=TRUE THEN 1 ELSE 0 END;
INSERT INTO "WorkMonitoringDecisions" ("WorkId", "IsMonitored") VALUES (:work_a,FALSE);
EXECUTE phase_b_monitoring_effective(:actor_id,:profile_id,ARRAY[:'public_a'::uuid]) \gset
SELECT 1 / CASE WHEN :'IsWorkMonitored'::boolean=FALSE THEN 1 ELSE 0 END;
DELETE FROM "WorkMonitoringDecisions" WHERE "WorkId"=:work_a AND "WorkTrackId" IS NULL;
INSERT INTO "WorkCredits" ("WorkEditionId", "PersonId", "WorkCreditRoleTypeId", "RoleName")
VALUES (:edition_a,:artist_id,2,'Director');
UPDATE "WorkMonitoringSources" SET "RoleNames"=ARRAY['director'] WHERE "Id"=:person_source;
EXECUTE phase_b_monitoring_effective(:actor_id,:profile_id,ARRAY[:'public_a'::uuid]) \gset
SELECT 1 / CASE WHEN :'IsWorkMonitored'::boolean=TRUE THEN 1 ELSE 0 END;
DELETE FROM "WorkMonitoringSources" WHERE "Id"=:person_source;
INSERT INTO "WorkMonitoringSources" ("MonitoringRelationTypeId", "StudioName", "AddedByProfileId")
VALUES (2,'a studio',:profile_id) RETURNING "Id" AS studio_source \gset
EXECUTE phase_b_monitoring_effective(:actor_id,:profile_id,ARRAY[:'public_a'::uuid]) \gset
SELECT 1 / CASE WHEN :'IsWorkMonitored'::boolean=TRUE THEN 1 ELSE 0 END;
DELETE FROM "WorkMonitoringSources" WHERE "Id"=:studio_source;
INSERT INTO "Collections" ("ProfileId", "Name") VALUES (:profile_id,'Lifecycle collection') RETURNING "Id" AS collection_id \gset
INSERT INTO "CollectionWorks" ("CollectionId", "WorkId") VALUES (:collection_id,:work_b);
INSERT INTO "WorkMonitoringSources" ("MonitoringRelationTypeId", "CollectionId", "AddedByProfileId")
VALUES (3,:collection_id,:profile_id) RETURNING "Id" AS collection_source \gset
EXECUTE phase_b_monitoring_effective(:actor_id,:profile_id,ARRAY[:'public_b'::uuid]) \gset
SELECT 1 / CASE WHEN :'IsWorkMonitored'::boolean=TRUE THEN 1 ELSE 0 END;
EXECUTE phase_b_monitoring_effective(:other_id,:other_profile,ARRAY[:'public_b'::uuid]);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_music_tracks(:other_id,:profile_id,:'edition_a_public',25,0);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
UPDATE "Accounts" SET "IsEnabled"=FALSE WHERE "Id"=:actor_id;
EXECUTE phase_b_monitoring_begin_artist(:actor_id,:profile_id,:'artist_public',gen_random_uuid(),TRUE,FALSE,now());
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
EXECUTE phase_b_monitoring_receipt(:actor_id,:profile_id,:'artist_public',:'future_command',TRUE,TRUE);
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
UPDATE "Accounts" SET "IsEnabled"=TRUE WHERE "Id"=:actor_id;
SET CONSTRAINTS ALL IMMEDIATE;
COMMIT;
