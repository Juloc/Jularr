using Jularr.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jularr.Web.Data.Migrations;

// Gives the open requests only what can be proven they asked for. The first version assumed a Work row for every open Movie, Book, Light Novel, Manga and
// Music request, which also made requests that Monitoring or the Wanted pass opened look explicit. What is provable: a Movie or TV request that still carries its
// choice (or has no payload, which the executor reads as the whole title) names exactly that; a request whose choice was applied earlier is not recoverable
// and stays driven by Monitoring. Requests of the other media types record their intent when they are submitted or approved from now on.
[DbContext(typeof(AppDbContext))]
[Migration("20261009100000_RequestTargetsRepair")]
public partial class RequestTargetsRepair : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DELETE FROM "RequestTargets";

            CREATE FUNCTION pg_temp.payload(body text) RETURNS jsonb LANGUAGE plpgsql AS $body$
            BEGIN
                RETURN CASE WHEN body IS NULL OR btrim(body) = '' THEN '{}'::jsonb ELSE body::jsonb END;
            EXCEPTION WHEN others THEN
                RETURN '{}'::jsonb;
            END
            $body$;

            CREATE TEMP TABLE request_choice AS
            SELECT r."Id" AS request_id,
                   COALESCE(r."WorkId"::uuid,
                            (pg_temp.payload(r."PayloadJson") ->> 'workId')::uuid,
                            (SELECT i."WorkId" FROM "WorkExternalIdentities" i WHERE i."Provider" = r."Provider" AND i."ExternalId" = r."ExternalId" LIMIT 1)) AS work_id,
                   CASE WHEN pg_temp.payload(r."PayloadJson") = '{}'::jsonb THEN '1' ELSE pg_temp.payload(r."PayloadJson") -> 'requested' ->> 'scope' END AS scope,
                   COALESCE(pg_temp.payload(r."PayloadJson") -> 'requested' -> 'episodeIds', '[]'::jsonb) AS episode_ids,
                   COALESCE(pg_temp.payload(r."PayloadJson") -> 'requested' -> 'seasonIds', '[]'::jsonb) AS season_ids
            FROM "AcquisitionRequests" r
            WHERE r."Kind" IN ('movie', 'tv') AND r."Status" IN ('approved', 'searching', 'downloading', 'importing');

            INSERT INTO "RequestTargets" ("RequestId", "WorkId", "TargetKind", "TargetId", "CreatedAt")
            SELECT choice.request_id, work."Id", 0, work."Id", now()
            FROM request_choice choice
            JOIN "Works" work ON work."Id" = choice.work_id
            WHERE choice.scope IN ('0', '1', 'WholeWork', 'AllCurrentAndFuture')
            ON CONFLICT DO NOTHING;

            INSERT INTO "RequestTargets" ("RequestId", "WorkId", "TargetKind", "TargetId", "CreatedAt")
            SELECT choice.request_id, episode."WorkId", 1, episode."Id", now()
            FROM request_choice choice
            JOIN "WorkEpisodes" episode ON episode."WorkId" = choice.work_id
            WHERE choice.scope IN ('3', 'Custom')
              AND (episode."Id"::text IN (SELECT jsonb_array_elements_text(choice.episode_ids))
                   OR episode."SeasonId"::text IN (SELECT jsonb_array_elements_text(choice.season_ids)))
            ON CONFLICT DO NOTHING;

            DROP TABLE request_choice;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
