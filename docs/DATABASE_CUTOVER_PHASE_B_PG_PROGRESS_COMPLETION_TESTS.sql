\set ON_ERROR_STOP on
\i /tmp/phase_b_progress_completion_prepared.sql
\i /tmp/phase_b_progress_checkpoint_prepared.sql
SELECT "Id" AS actor_a FROM "Accounts" WHERE "Email"='checkpoint-a@example.invalid' \gset
SELECT "Id" AS actor_b FROM "Accounts" WHERE "Email"='checkpoint-b@example.invalid' \gset
SELECT "Id" AS profile_id FROM "Profiles" WHERE "DisplayName"='Checkpoint A' \gset
BEGIN;
INSERT INTO "Works" ("MediaTypeId", "CanonicalTitle") VALUES (2, 'Completion series')
RETURNING "Id" AS work_id, "PublicId" AS work_public \gset
INSERT INTO "WorkSeasons" ("WorkId", "SeasonNumber") VALUES (:work_id, 0), (:work_id, 1), (:work_id, 2);
INSERT INTO "WorkEpisodes" ("WorkId", "WorkSeasonId", "EpisodeNumber", "OrderIndex")
SELECT :work_id, season."Id", episode_number, episode_number
FROM "WorkSeasons" AS season CROSS JOIN generate_series(1,3) AS episode_number
WHERE season."WorkId"=:work_id;
INSERT INTO "WorkEpisodes" ("WorkId", "WorkSeasonId", "EpisodeNumber", "OrderIndex", "IsSpecial")
SELECT :work_id, season."Id", 0.5, 0, true FROM "WorkSeasons" AS season
WHERE season."WorkId"=:work_id AND season."SeasonNumber"=1;
INSERT INTO "MediaProgress" (
    "ProfileId", "WorkId", "WorkEpisodeId", "ProgressPositionTypeId", "IsCompleted",
    "CompletionReasonTypeId", "CompletedAt", "CompletedByAccountId")
SELECT :profile_id, :work_id, episode."Id", 1, true, 3, now(), :actor_a
FROM "WorkEpisodes" AS episode INNER JOIN "WorkSeasons" AS season ON season."Id"=episode."WorkSeasonId"
WHERE episode."WorkId"=:work_id
    AND (season."SeasonNumber"=1 AND episode."EpisodeNumber" IN (1,3)
        OR season."SeasonNumber"=2 AND episode."EpisodeNumber"=1);
INSERT INTO "TimeProgressPositions" ("MediaProgressId")
SELECT progress."Id" FROM "MediaProgress" AS progress WHERE progress."WorkId"=:work_id;
COMMIT;
SELECT episode."PublicId" AS first_episode_public FROM "WorkEpisodes" AS episode
INNER JOIN "WorkSeasons" AS season ON season."Id"=episode."WorkSeasonId"
WHERE episode."WorkId"=:work_id AND season."SeasonNumber"=1 AND episode."EpisodeNumber"=1 \gset
SELECT episode."PublicId" AS next_season_first_public FROM "WorkEpisodes" AS episode
INNER JOIN "WorkSeasons" AS season ON season."Id"=episode."WorkSeasonId"
WHERE episode."WorkId"=:work_id AND season."SeasonNumber"=2 AND episode."EpisodeNumber"=1 \gset
EXECUTE phase_b_progress_completion(:actor_a,:profile_id,:'work_public') \gset
SELECT 1 / CASE WHEN :'CompletedThroughEpisodeId'::uuid=:'first_episode_public'::uuid THEN 1 ELSE 0 END;
EXECUTE phase_b_progress_completion(:actor_b,:profile_id,:'work_public');
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;

BEGIN;
INSERT INTO "MediaProgress" ("ProfileId", "WorkId", "WorkEpisodeId", "ProgressPositionTypeId")
SELECT :profile_id, :work_id, episode."Id", 1
FROM "WorkEpisodes" AS episode INNER JOIN "WorkSeasons" AS season ON season."Id"=episode."WorkSeasonId"
WHERE episode."WorkId"=:work_id AND season."SeasonNumber"=1 AND episode."EpisodeNumber"=2
RETURNING "Id" AS gap_progress_id, "PublicId" AS gap_progress_public \gset
INSERT INTO "TimeProgressPositions" ("MediaProgressId") VALUES (:gap_progress_id);
EXECUTE phase_b_progress_checkpoint(:actor_a,:profile_id,:'gap_progress_public',1,gen_random_uuid(),
    decode(repeat('61',32),'hex'),3,now(),NULL,3,false,0,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL);
COMMIT;
EXECUTE phase_b_progress_completion(:actor_a,:profile_id,:'work_public') \gset
SELECT 1 / CASE WHEN :'CompletedThroughEpisodeId'::uuid=:'next_season_first_public'::uuid THEN 1 ELSE 0 END;

-- A manual unmark reopens the first gap; later completed episodes do not hide it.
SELECT progress."PublicId" AS first_progress_public FROM "MediaProgress" AS progress
INNER JOIN "WorkEpisodes" AS episode ON episode."Id"=progress."WorkEpisodeId"
WHERE episode."PublicId"=:'first_episode_public' \gset
EXECUTE phase_b_progress_checkpoint(:actor_a,:profile_id,:'first_progress_public',1,gen_random_uuid(),
    decode(repeat('62',32),'hex'),3,now(),NULL,NULL,true,0,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL);
EXECUTE phase_b_progress_completion(:actor_a,:profile_id,:'work_public') \gset
SELECT 1 / CASE WHEN NOT :{?CompletedThroughEpisodeId} AND :{?UpdatedAt} THEN 1 ELSE 0 END;
BEGIN;
UPDATE "Accounts" SET "IsEnabled"=false WHERE "Id"=:actor_a;
EXECUTE phase_b_progress_completion(:actor_a,:profile_id,:'work_public');
SELECT 1 / CASE WHEN :ROW_COUNT=0 THEN 1 ELSE 0 END;
ROLLBACK;
ANALYZE "WorkEpisodes";
EXPLAIN (ANALYZE, BUFFERS, TIMING FALSE) EXECUTE phase_b_progress_completion(:actor_a,:profile_id,:'work_public');
DEALLOCATE phase_b_progress_checkpoint;
DEALLOCATE phase_b_progress_completion;
