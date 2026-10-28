using System.Globalization;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/works/{workId:long}/tvdb-preview")]
    public sealed class ReadTvdbSeriesPreviewV1 : AdminService<WorkParametersV1>
    {
        private readonly TvdbMetadata _tvdb;

        internal ReadTvdbSeriesPreviewV1(TvdbMetadata tvdb)
        {
            _tvdb = tvdb;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<TvdbSeriesPreviewV1>()];
        protected override ModuleRequirement GetInstanceModules(WorkParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(WorkParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnRead()
        {
            var work = await WorkMetadataQueries.ReadWorkAsync(Context, Parameters.WorkId, CancellationToken);
            if (work.MediaTypeId != MediaType.Series)
            {
                throw new DataValidationException(nameof(WorkParametersV1.WorkId), "unknown_reference");
            }

            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    identity."Namespace",
                    identity."Value",
                    identity."WorkId"
                FROM
                    "WorkExternalIdentities" AS identity
                WHERE
                    identity."WorkId" = @Id
                    AND identity."Namespace" IN ('tvdb', 'imdb');
                """;
            sql.Parameters.Add(new WorkIdData(work.Id));
            var identities = await Context.ReadSql.ReadListAsync<WorkIdentityData>(sql, CancellationToken);
            var imdbId = identities.FirstOrDefault(identity => identity.Namespace == "imdb")?.Value;
            var tvdb = identities.FirstOrDefault(identity => identity.Namespace == "tvdb")?.Value;

            var session = await _tvdb.ConnectAsync(Context, CancellationToken);
            int? tvdbId = tvdb is null
                ? imdbId is null ? null : await _tvdb.FindSeriesByImdbAsync(session, imdbId, CancellationToken)
                : int.TryParse(tvdb, NumberStyles.None, CultureInfo.InvariantCulture, out var knownId) && knownId > 0
                    ? knownId
                    : throw new DataValidationException("TvdbId", "invalid_type");
            if (tvdbId is null)
            {
                throw new DataValidationException("TvdbId", "unknown_reference");
            }

            var series = await _tvdb.ReadSeriesAsync(session, tvdbId.Value, CancellationToken);
            if (imdbId is not null && series.ImdbId is not null && !string.Equals(imdbId, series.ImdbId, StringComparison.OrdinalIgnoreCase))
            {
                throw new DataValidationException("ImdbId", "in_use");
            }

            var remote = await _tvdb.ReadEpisodesAsync(session, tvdbId.Value, CancellationToken);
            if (remote.GroupBy(item => (item.SeasonNumber, item.EpisodeNumber)).Any(group => group.Count() != 1))
            {
                throw new DataValidationException("TvdbId", "in_use");
            }

            sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    episode."Id",
                    season."Number"::integer AS "SeasonNumber",
                    episode."Number"::integer AS "EpisodeNumber",
                    episode."Title"
                FROM
                    "WorkUnits" AS episode
                    JOIN "WorkUnits" AS season ON season."Id" = episode."ParentWorkUnitId"
                WHERE
                    episode."WorkId" = @Id
                    AND episode."WorkUnitTypeId" = 2
                    AND season."WorkUnitTypeId" = 1
                    AND season."Number" = trunc(season."Number")
                    AND episode."Number" = trunc(episode."Number");
                """;
            sql.Parameters.Add(new WorkIdData(work.Id));
            var local = await Context.ReadSql.ReadListAsync<TvdbLocalEpisodeData>(sql, CancellationToken);
            var indexed = local.ToDictionary(item => (item.SeasonNumber, item.EpisodeNumber));
            var compared = remote.Select(item =>
            {
                indexed.TryGetValue((item.SeasonNumber, item.EpisodeNumber), out var existing);
                return new TvdbEpisodeComparisonV1(item.SeasonNumber, item.EpisodeNumber, item.Id, existing?.Id,
                    TextLimit.Cut(item.Name, 500), existing?.Title, existing is null ? "new" : "review");
            }).ToList();
            var remoteNumbers = remote.Select(item => (item.SeasonNumber, item.EpisodeNumber)).ToHashSet();
            compared.AddRange(local.Where(item => !remoteNumbers.Contains((item.SeasonNumber, item.EpisodeNumber)))
                .Select(item => new TvdbEpisodeComparisonV1(item.SeasonNumber, item.EpisodeNumber, null, item.Id, null, item.Title, "local_only")));

            return new TvdbSeriesPreviewV1(work.Id, tvdbId.Value, TextLimit.Cut(series.Name, 500)!, imdbId, compared
                .OrderBy(item => item.SeasonNumber).ThenBy(item => item.EpisodeNumber).ToArray());
        }
    }
}
