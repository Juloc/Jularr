using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Admin.Media.V1;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.System.Media.V1
{
    // Titles that came without their facts (a media server's sync names only the title, the year and ids) or without details get their description, cover and cast
    // from the catalog, and a title opened while its details were more than a day old gets them again; a few at a time, true while more are waiting.
    // A title is tried once a day (MetadataTriedAt): one the catalog cannot give anything stays as it is and never holds back the others. Each type takes its own
    // turn, so a catalog that cannot answer (TheTVDB for series, TMDB for films) holds back only its own titles, and series keep to TheTVDB's requests a minute.
    public sealed class FillWorkMetadataV1 : SystemService<CoverParametersV1, NoData>
    {
        private const int PassSize = 3;

        // A series import asks TheTVDB a few times and looks up the people it credits; it starts only while this much of the minute's budget is left.
        private const int SeriesRequests = 10;
        private readonly WorkMetadata _metadata;
        private readonly BookMetadata _books;
        private readonly AniListMetadata _aniList;
        private readonly MusicMetadata _music;
        private readonly GameMetadata _games;
        private readonly TvdbMetadata _tvdb;

        internal FillWorkMetadataV1(WorkMetadata metadata, BookMetadata books, AniListMetadata aniList, MusicMetadata music, GameMetadata games, TvdbMetadata tvdb)
        {
            _metadata = metadata;
            _books = books;
            _aniList = aniList;
            _music = music;
            _games = games;
            _tvdb = tvdb;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(CoverParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(CoverParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Works with details that have neither a description nor a cover or were opened while their details were a day old, and Works of the library without
            // details; a catalog title nobody opened is no such Work. Only Works with an identity their type's catalog can read count, a few of each type in turns,
            // and none tried within the last day.
            var (waiting, budget) = await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        ranked."Id",
                        ranked."MediaTypeId"
                    FROM
                        (
                            SELECT
                                due."Id",
                                due."MediaTypeId",
                                row_number() OVER (PARTITION BY due."MediaTypeId" ORDER BY due."Id") AS "Place"
                            FROM
                                (
                                    SELECT
                                        work."Id",
                                        work."MediaTypeId"
                                    FROM
                                        "Works" AS work
                                    WHERE
                                        work."DetailedAt" IS NOT NULL
                                        AND work."MediaTypeId" IN (1, 2, 3, 6, 7)
                                        AND (work."DetailedAt" < work."ViewedAt" - interval '1 day' OR NOT EXISTS (
                                            SELECT
                                                1
                                            FROM
                                                "WorkMetadataFacts" AS fact
                                            WHERE
                                                fact."WorkId" = work."Id"
                                                AND fact."FactKey" IN ('Overview', 'Poster')
                                        ))
                                    UNION
                                    SELECT
                                        work."Id",
                                        work."MediaTypeId"
                                    FROM
                                        "WorkLibraryRelations" AS library
                                        JOIN "Works" AS work ON work."Id" = library."WorkId"
                                    WHERE
                                        work."DetailedAt" IS NULL
                                        AND work."MediaTypeId" IN (1, 2, 3, 6, 7)
                                ) AS due
                                JOIN "Works" AS work ON work."Id" = due."Id"
                            WHERE
                                (work."MetadataTriedAt" IS NULL OR work."MetadataTriedAt" < now() - interval '1 day')
                                AND EXISTS (
                                    SELECT
                                        1
                                    FROM
                                        "WorkExternalIdentities" AS identity
                                    WHERE
                                        identity."WorkId" = work."Id"
                                        AND (identity."Namespace" IN ('tmdb_movie', 'openlibrary', 'musicbrainz', 'igdb')
                                            OR identity."Namespace" IN ('tvdb', 'tmdb_tv', 'imdb') AND work."MediaTypeId" = 2
                                            OR identity."Namespace" = 'imdb' AND work."MediaTypeId" = 1
                                            OR identity."Namespace" = 'anilist' AND work."MediaTypeId" = 3)
                                )
                        ) AS ranked
                    WHERE
                        ranked."Place" <= 4
                    ORDER BY
                        ranked."Place",
                        ranked."MediaTypeId";
                    """;
                return (await ctx.ReadSql.ReadListAsync<WorkIdentityCandidateData>(sql, ct), (await ctx.ReadExternalWorkSyncAsync("tvdb", ct))?.RequestsPerMinute ?? 0);
            }, CancellationToken);
            var unavailable = new HashSet<MediaType>();
            var filled = 0;
            foreach (var work in waiting)
            {
                // A catalog that is not there now holds back only its own kind of title; series also wait while TheTVDB's budget of this minute runs low.
                if (filled == PassSize) break;
                if (unavailable.Contains(work.MediaTypeId)) continue;
                if (work.MediaTypeId == MediaType.Series && !_tvdb.HasBudget(budget, SeriesRequests))
                {
                    unavailable.Add(MediaType.Series);
                    continue;
                }

                filled++;
                try
                {
                    await FillAsync(work.Id);
                }
                catch (ServiceUnavailableException)
                {
                    // The title is not marked and the next pass asks again.
                    unavailable.Add(work.MediaTypeId);
                    continue;
                }
                catch (Exception exception) when (exception is KeyNotFoundException or DataValidationException)
                {
                    // The catalog names nothing for this title, or its ids belong to another Work of the library.
                }

                await Context.ExecuteBatchAsync(async (ctx, ct) =>
                {
                    await ctx.MarkWorkMetadataTriedAsync(work.Id, ct);
                    return true;
                }, CancellationToken);
            }

            return new UpdateResult(unavailable.Count == 0 && waiting.Count > filled);
        }

        // A Work with a provider identity is refreshed like the Work page's Refresh, a series by its TheTVDB, TMDB or IMDb id; a film known only by its IMDb id
        // is found through TMDB first.
        private async Task FillAsync(long workId)
        {
            var known = await Context.ExecuteBatchAsync((ctx, ct) => WorkMetadataQueries.ReadProviderIdentityAsync(ctx, workId, ct), CancellationToken);
            if (known is not null)
            {
                await RefreshWorkMetadataV1.RefreshAsync(Context, _metadata, _books, _aniList, _music, _games, workId, CancellationToken);
                return;
            }

            var work = await Context.ExecuteBatchAsync((ctx, ct) => WorkMetadataQueries.ReadWorkAsync(ctx, workId, ct), CancellationToken);
            if (work.MediaTypeId != MediaType.Movie)
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
                        AND identity."Namespace" = 'imdb';
                    """;
                sql.Parameters.Add(new WorkIdData(workId));
                return ctx.ReadSql.ReadListAsync<WorkIdentityProbeData>(sql, ct);
            }, CancellationToken);
            var connection = await _metadata.ConnectAsync(Context, CancellationToken);
            foreach (var identity in identities)
            {
                if (await _metadata.FindExternalAsync(connection, work.MediaTypeId, identity.Namespace, identity.Value, CancellationToken) is { } tmdbId)
                {
                    await _metadata.ImportAsync(Context, connection, work.MediaTypeId, null, tmdbId, CancellationToken);
                    return;
                }
            }
        }
    }
}
