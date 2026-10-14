using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.User.Statistics.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Statistics.V1
{
    [ServiceEndpoint("GET", "/api/v1/statistics")]
    public sealed class ReadProfileStatisticsV1 : UserService<ReadStatisticsParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ProfileStatisticsV1>()];
        protected override ModuleRequirement GetInstanceModules(ReadStatisticsParametersV1 parameters) => ModuleRequirement.All("Library", "Statistics");
        protected override ServicePermission GetPermission(ReadStatisticsParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            // Stored history only; 0 days means all of it.
            var scope = new StatisticsScopeData(Auth.ProfileId!.Value, Parameters.Days);
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    coalesce((
                        SELECT
                            sum(extract(epoch FROM coalesce(session."EndedAt", session."LastSeenAt") - session."StartedAt") * 1000)::bigint
                        FROM
                            "PlaybackSessions" AS session
                        WHERE
                            session."ProfileId" = @ProfileId
                            AND (@Days = 0 OR session."StartedAt" >= now() - make_interval(days => @Days))
                    ), 0) AS "WatchedMs",
                    (
                        SELECT
                            count(*)
                        FROM
                            "PlaybackSessions" AS session
                        WHERE
                            session."ProfileId" = @ProfileId
                            AND (@Days = 0 OR session."StartedAt" >= now() - make_interval(days => @Days))
                    ) AS "Sessions",
                    coalesce((
                        SELECT
                            sum(progress."PositionMs")
                        FROM
                            "MediaProgress" AS progress
                        WHERE
                            progress."ProfileId" = @ProfileId
                            AND progress."ProgressPositionTypeId" = 3
                    ), 0)::bigint AS "GamePlayedMs";
                """;
            sql.Parameters.Add(scope);
            var totals = await Context.ReadSql.ReadRequiredAsync<ProfileTotalsData>(sql, CancellationToken);

            // Finished titles and units by media type.
            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."MediaTypeId",
                    count(*) AS "Count"
                FROM
                    "MediaProgress" AS progress
                    JOIN "Works" AS work ON work."Id" = progress."WorkId"
                WHERE
                    progress."ProfileId" = @ProfileId
                    AND progress."IsCompleted"
                    AND (@Days = 0 OR progress."CompletedAt" >= now() - make_interval(days => @Days))
                GROUP BY
                    work."MediaTypeId"
                ORDER BY
                    work."MediaTypeId";
                """;
            sql.Parameters.Add(scope);
            var completed = await Context.ReadSql.ReadListAsync<MediaTypeCompletedV1>(sql, CancellationToken);

            // The five Works with the most time in the player.
            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id" AS "WorkId",
                    work."CanonicalTitle" AS "Title",
                    sum(extract(epoch FROM coalesce(session."EndedAt", session."LastSeenAt") - session."StartedAt") * 1000)::bigint AS "DurationMs"
                FROM
                    "PlaybackSessions" AS session
                    JOIN "MediaAssets" AS asset ON asset."Id" = session."MediaAssetId"
                    JOIN "Works" AS work ON work."Id" = asset."WorkId"
                WHERE
                    session."ProfileId" = @ProfileId
                    AND (@Days = 0 OR session."StartedAt" >= now() - make_interval(days => @Days))
                GROUP BY
                    work."Id",
                    work."CanonicalTitle"
                ORDER BY
                    "DurationMs" DESC,
                    work."Id"
                LIMIT 5;
                """;
            sql.Parameters.Add(scope);
            var top = await Context.ReadSql.ReadListAsync<WorkTimeV1>(sql, CancellationToken);
            return new ProfileStatisticsV1(totals.WatchedMs, totals.Sessions, totals.GamePlayedMs, new BaseDataList<MediaTypeCompletedV1>(completed), new BaseDataList<WorkTimeV1>(top));
        }
    }
}
