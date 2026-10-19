using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Integrations.Data.AniList;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    // Finds the AniList entries of one canonical Work: the Work's own entry, or for a series its seasons along the sequel chain.
    internal sealed class AniListMatcher(AniListMetadata aniList)
    {
        private const int ChainLimit = 30;

        public async Task<IdentityMatchResultData> MatchAsync(IdentityMatchTargetData target, IReadOnlyList<SeasonEpisodesData> seasons, int? chosenId, CancellationToken ct)
        {
            var type = AniListMatching.Type(target.MediaTypeId, target.MediaSubtypeId) ?? throw new KeyNotFoundException("The Work has no AniList kind.");
            var formats = AniListMatching.Formats(target.MediaTypeId, target.MediaSubtypeId);

            // A person's choice is taken as it is; otherwise one clear candidate is.
            AniListMedia? match;
            IReadOnlyList<AniListMedia> candidates;
            if (chosenId is { } id)
            {
                match = await aniList.ReadAsync(type, id, ct);
                candidates = [match];
            }
            else
            {
                (match, candidates) = AniListMatching.Choose(await aniList.SearchAsync(type, formats, target.CanonicalTitle, ct), [target.CanonicalTitle, .. target.Titles], target.ReleaseYear, formats);
            }

            var listed = JsonSerializer.Serialize(candidates.Select(candidate => new { candidate.Id, Title = AniListMetadata.Title(candidate), candidate.Year, candidate.Format }));
            var manual = chosenId is not null;
            if (match is null)
            {
                return Result(candidates.Count == 0 ? IdentityMatchStatusType.NoMatch : IdentityMatchStatusType.Review, null, "[]");
            }

            if (target.MediaTypeId != MediaType.Series)
            {
                return Result(IdentityMatchStatusType.Matched, match.Id.ToString(CultureInfo.InvariantCulture), "[]");
            }

            // A series follows its first sequel of a series format, season after season.
            var chain = new List<(int Id, int? Episodes)> { (match.Id, match.Episodes) };
            var current = match;
            while (chain.Count < ChainLimit && current.Sequels.Count > 0)
            {
                AniListMedia? next = null;
                foreach (var sequel in current.Sequels)
                {
                    // A sequel AniList no longer knows is skipped.
                    AniListMedia candidate;
                    try
                    {
                        candidate = await aniList.ReadAsync(type, sequel, ct);
                    }
                    catch (KeyNotFoundException)
                    {
                        continue;
                    }

                    if (candidate.Format is { } format && formats.Contains(format) && chain.All(entry => entry.Id != candidate.Id))
                    {
                        next = candidate;
                        break;
                    }
                }

                if (next is null)
                {
                    break;
                }

                chain.Add((next.Id, next.Episodes));
                current = next;
            }

            // A chosen entry that fits no season is refused instead of waiting again.
            var aligned = AniListMatching.Align([.. seasons.Select(season => (season.SeasonNumber, season.Episodes))], chain);
            if (aligned.Count == 0 && manual)
            {
                throw new DataValidationException("AniListId", "invalid_transition");
            }

            return aligned.Count == 0
                ? Result(IdentityMatchStatusType.Review, null, "[]")
                : Result(IdentityMatchStatusType.Matched, null, JsonSerializer.Serialize(aligned.Select(entry =>
                    new { entry.Season, Value = entry.AniListId.ToString(CultureInfo.InvariantCulture), entry.Offset })));

            IdentityMatchResultData Result(IdentityMatchStatusType status, string? value, string mapped) =>
                new(target.WorkId, AniListMetadata.Namespace, status, listed, value, mapped, manual);
        }

        // New Works first, then automatic series for further seasons and earlier misses; one Work only while it waits for a person.
        public static Task<IReadOnlyList<IdentityMatchTargetData>> ReadTargetsAsync(LogicContext ctx, long? workId, int limit, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    work."Id" AS "WorkId",
                    work."MediaTypeId",
                    work."MediaSubtypeId",
                    work."CanonicalTitle",
                    work."ReleaseYear"::integer AS "ReleaseYear",
                    ARRAY(
                        SELECT DISTINCT
                            title."Title"
                        FROM
                            "WorkTitles" AS title
                        WHERE
                            title."WorkId" = work."Id"
                            AND title."Title" <> work."CanonicalTitle"
                    ) AS "Titles"
                FROM
                    "Works" AS work
                    LEFT JOIN "WorkIdentityMatches" AS match ON match."WorkId" = work."Id" AND match."Namespace" = 'anilist'
                WHERE
                    (work."Id" = @WorkId AND match."IdentityMatchStatusTypeId" = 2
                        OR @WorkId::bigint IS NULL
                        AND (match."Id" IS NULL
                            OR match."IdentityMatchStatusTypeId" = 1 AND work."MediaTypeId" = 2 AND match."CheckedAt" < now() - interval '7 days' AND NOT EXISTS (
                                SELECT
                                    1
                                FROM
                                    "WorkUnits" AS season
                                    JOIN "WorkUnitExternalIdentities" AS mapped ON mapped."WorkUnitId" = season."Id"
                                WHERE
                                    season."WorkId" = work."Id"
                                    AND mapped."Namespace" = 'anilist'
                                    AND mapped."IsManual"
                            )
                            OR match."IdentityMatchStatusTypeId" = 3 AND match."CheckedAt" < now() - interval '30 days'))
                    AND (work."MediaSubtypeId" = 1 AND work."MediaTypeId" IN (1, 2) OR work."MediaSubtypeId" IN (2, 3))
                    AND NOT EXISTS (
                        SELECT
                            1
                        FROM
                            "WorkExternalIdentities" AS identity
                        WHERE
                            identity."WorkId" = work."Id"
                            AND identity."Namespace" = 'anilist'
                    )
                ORDER BY
                    match."CheckedAt" NULLS FIRST,
                    work."Id"
                LIMIT @Limit;
                """;
            sql.Parameters.Add(new IdentityMatchQueryData(workId, limit));
            return ctx.ReadSql.ReadListAsync<IdentityMatchTargetData>(sql, ct);
        }

        // Regular seasons with the number of their episodes.
        public static Task<IReadOnlyList<SeasonEpisodesData>> ReadSeasonsAsync(LogicContext ctx, long workId, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    season."Number"::integer AS "SeasonNumber",
                    count(episode."Id")::integer AS "Episodes"
                FROM
                    "WorkUnits" AS season
                    LEFT JOIN "WorkUnits" AS episode ON episode."ParentWorkUnitId" = season."Id" AND episode."WorkUnitTypeId" = 2
                WHERE
                    season."WorkId" = @Id
                    AND season."WorkUnitTypeId" = 1
                    AND season."Number" > 0
                GROUP BY
                    season."Number"
                ORDER BY
                    season."Number";
                """;
            sql.Parameters.Add(new WorkIdData(workId));
            return ctx.ReadSql.ReadListAsync<SeasonEpisodesData>(sql, ct);
        }
    }
}
