using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Backend.Service.Core;
using Microsoft.Extensions.Caching.Memory;

namespace Jularr.Backend.Service.System.Media.V1
{
    // Titles that came without their facts (a media server's sync names only the title, the year and ids) get their description, cover and cast from the catalog,
    // and a title opened while its details were more than a day old gets them again; a few at a time, true while more are waiting.
    // A title is tried once a day: one the catalog cannot give anything stays as it is.
    public sealed class FillWorkMetadataV1 : SystemService<CoverParametersV1, NoData>
    {
        private const int PassSize = 3;
        private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(24);
        private readonly WorkMetadata _metadata;
        private readonly BookMetadata _books;
        private readonly AniListMetadata _aniList;
        private readonly MusicMetadata _music;
        private readonly GameMetadata _games;
        private readonly IMemoryCache _cache;

        internal FillWorkMetadataV1(WorkMetadata metadata, BookMetadata books, AniListMetadata aniList, MusicMetadata music, GameMetadata games, IMemoryCache cache)
        {
            _metadata = metadata;
            _books = books;
            _aniList = aniList;
            _music = music;
            _games = games;
            _cache = cache;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(CoverParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(CoverParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Works with an identity a catalog knows and neither a description nor a cover, or opened with details a day old; a catalog title nobody opened is no such Work.
            var waiting = await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        work."Id"
                    FROM
                        "Works" AS work
                    WHERE
                        work."MediaTypeId" IN (1, 2, 3, 6, 7)
                        AND (work."DetailedAt" IS NOT NULL OR EXISTS (SELECT 1 FROM "WorkLibraryRelations" AS library WHERE library."WorkId" = work."Id"))
                        AND (work."DetailedAt" < work."ViewedAt" - interval '1 day' OR NOT EXISTS (
                            SELECT
                                1
                            FROM
                                "WorkMetadataFacts" AS fact
                            WHERE
                                fact."WorkId" = work."Id"
                                AND fact."FactKey" IN ('Overview', 'Poster')
                        ))
                        AND EXISTS (
                            SELECT
                                1
                            FROM
                                "WorkExternalIdentities" AS identity
                            WHERE
                                identity."WorkId" = work."Id"
                                AND identity."Namespace" IN ('tmdb_movie', 'tmdb_tv', 'openlibrary', 'musicbrainz', 'igdb', 'anilist', 'imdb', 'tvdb')
                        )
                    ORDER BY
                        work."Id"
                    LIMIT 200;
                    """;
                return ctx.ReadSql.ReadListAsync<WorkIdData>(sql, ct);
            }, CancellationToken);
            var due = waiting.Where(work => !_cache.TryGetValue(Key(work.Id), out _)).ToArray();
            foreach (var work in due.Take(PassSize))
            {
                try
                {
                    await FillAsync(work.Id);
                }
                catch (ServiceUnavailableException)
                {
                    // The provider is not there now; the title is not marked and the next pass asks again.
                    return new UpdateResult(false);
                }
                catch (Exception exception) when (exception is KeyNotFoundException or DataValidationException)
                {
                    // The catalog names nothing for this title.
                }

                _cache.Set(Key(work.Id), true, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = RetryAfter, Size = 1 });
            }

            return new UpdateResult(due.Length > PassSize);
        }

        private static string Key(long workId) => "metadata.fill." + workId.ToString(CultureInfo.InvariantCulture);

        // A Work with a provider identity is refreshed like the Work page's Refresh; a film or series known only by its IMDb or TheTVDB id is found through TMDB first.
        private async Task FillAsync(long workId)
        {
            var known = await Context.ExecuteBatchAsync((ctx, ct) => WorkMetadataQueries.ReadProviderIdentityAsync(ctx, workId, ct), CancellationToken);
            if (known is not null)
            {
                await RefreshWorkMetadataV1.RefreshAsync(Context, _metadata, _books, _aniList, _music, _games, workId, CancellationToken);
                return;
            }

            var work = await Context.ExecuteBatchAsync((ctx, ct) => WorkMetadataQueries.ReadWorkAsync(ctx, workId, ct), CancellationToken);
            if (work.MediaTypeId is not (MediaType.Movie or MediaType.Series))
            {
                return;
            }

            var identities = await Context.ExecuteBatchAsync((ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        identity."Namespace",
                        identity."Value"
                    FROM
                        "WorkExternalIdentities" AS identity
                    WHERE
                        identity."WorkId" = @Id
                        AND identity."Namespace" IN ('imdb', 'tvdb');
                    """;
                sql.Parameters.Add(new WorkIdData(workId));
                return ctx.ReadSql.ReadListAsync<WorkIdentityProbeData>(sql, ct);
            }, CancellationToken);
            var connection = await _metadata.ConnectAsync(Context, CancellationToken);
            foreach (var identity in identities.OrderBy(item => item.Namespace == "imdb" ? 0 : 1))
            {
                if (await _metadata.FindExternalAsync(connection, work.MediaTypeId, identity.Namespace, identity.Value, CancellationToken) is { } tmdbId)
                {
                    await _metadata.ImportAsync(Context, connection, work.MediaTypeId, null, tmdbId, CancellationToken,
                        work.MediaTypeId == MediaType.Series && identities.FirstOrDefault(item => item.Namespace == "tvdb") is { } tvdb && int.TryParse(tvdb.Value, out var tvdbId) ? tvdbId : null);
                    return;
                }
            }
        }
    }
}
