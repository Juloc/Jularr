using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Provider;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Logic.Provider;
using Jularr.Backend.Service.Admin.Provider.V1;
using Jularr.Integrations.Data.Tmdb;
using Jularr.Integrations.Logic.Tmdb;
using Microsoft.AspNetCore.DataProtection;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    // TMDB lookups and imports; shared by Admin catalog work and the System refresh.
    internal sealed class WorkMetadata
    {
        private readonly TmdbClient _tmdb;
        private readonly IDataProtector _protector;
        private readonly DiscoverySnapshots _snapshots;
        private readonly ProviderOptions _providers;

        public WorkMetadata(TmdbClient tmdb, IDataProtectionProvider protection, DiscoverySnapshots snapshots, ProviderOptions providers)
        {
            _tmdb = tmdb;
            _snapshots = snapshots;
            _providers = providers;
            _protector = protection.CreateProtector(SaveProviderSecretV1.ProtectorPurpose);
        }

        public static string Namespace(MediaType mediaType) => mediaType == MediaType.Movie ? "tmdb_movie" : "tmdb_tv";

        public async Task<TmdbConnection> ConnectAsync(LogicContext context, CancellationToken ct)
        {
            var (credential, locale) = await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                var sql = new SqlStatement();
                sql.Statement =
                    """
                    SELECT
                        locale."Locale"
                    FROM
                        "InstanceSettings" AS settings
                        JOIN "UiLocales" AS locale ON locale."Id" = settings."UiLocaleId";
                    """;
                var language = await ctx.ReadSql.ReadOptionalAsync<MetadataLanguageData>(sql, token);
                return (await ctx.ReadProviderCredentialAsync(new ProviderParameters("tmdb"), token), language?.Locale ?? "en");
            }, ct);

            // Without an enabled key there is no metadata provider.
            if (credential is not { IsEnabled: true, ProtectedSecret: { } secret })
            {
                throw new ServiceUnavailableException("metadata_unavailable");
            }

            byte[] clear;
            try
            {
                clear = _protector.Unprotect(secret);
            }
            catch (CryptographicException)
            {
                throw new ServiceUnavailableException("metadata_unavailable");
            }

            try
            {
                return new TmdbConnection(Encoding.UTF8.GetString(clear), locale);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }

        public Task<IReadOnlyList<TmdbSearchResult>> SearchAsync(LogicContext context, TmdbConnection connection, MediaType mediaType, string query, int? year, CancellationToken ct, int page = 1) =>
            ReadSnapshotAsync<IReadOnlyList<TmdbSearchResult>>(context, new DiscoverySnapshotRequest { Operation = "tmdb.search", Origin = _providers.Tmdb.AbsoluteUri,
                MediaType = mediaType, Language = connection.Language, Query = query.Trim(), Year = year, Page = page }, connection, ct);

        public Task<IReadOnlyList<TmdbSearchResult>> ReadFeedAsync(LogicContext context, TmdbConnection connection, MediaType mediaType, DiscoverFeedType feed, int page, CancellationToken ct) =>
            ReadSnapshotAsync<IReadOnlyList<TmdbSearchResult>>(context, new DiscoverySnapshotRequest { Operation = "tmdb.feed", Origin = _providers.Tmdb.AbsoluteUri,
                MediaType = mediaType, Language = connection.Language, Feed = feed, Page = page }, connection, ct);

        public Task<IReadOnlyList<TmdbSearchResult>> ReadRecommendationsAsync(LogicContext context, TmdbConnection connection, MediaType mediaType, int tmdbId, CancellationToken ct) =>
            ReadSnapshotAsync<IReadOnlyList<TmdbSearchResult>>(context, new DiscoverySnapshotRequest { Operation = "tmdb.recommendations", Origin = _providers.Tmdb.AbsoluteUri,
                MediaType = mediaType, Language = connection.Language, Id = tmdbId }, connection, ct);

        public Task<TmdbWork> ReadWorkAsync(LogicContext context, TmdbConnection connection, MediaType mediaType, int tmdbId, CancellationToken ct, bool forceRefresh = false) =>
            ReadSnapshotAsync<TmdbWork>(context, new DiscoverySnapshotRequest { Operation = "tmdb.work", Origin = _providers.Tmdb.AbsoluteUri,
                MediaType = mediaType, Language = connection.Language, Id = tmdbId }, connection, ct, forceRefresh);

        private Task<T> ReadSnapshotAsync<T>(LogicContext context, DiscoverySnapshotRequest request, TmdbConnection connection, CancellationToken ct, bool forceRefresh = false) =>
            _snapshots.ReadAsync(context, request, async token => (T)await FetchSnapshotAsync(request, connection, token), ct, forceRefresh);

        internal async Task<object> FetchSnapshotAsync(DiscoverySnapshotRequest request, TmdbConnection connection, CancellationToken ct)
        {
            try
            {
                var region = connection.Language.Split('-') is [_, { Length: 2 } country] ? country.ToUpperInvariant() : null;
                return request.Operation switch
                {
                    "tmdb.search" => await _tmdb.SearchAsync(connection, Kind(request.MediaType), request.Query!, request.Year, ct, request.Page),
                    "tmdb.feed" => await _tmdb.ReadFeedAsync(connection, Kind(request.MediaType), (TmdbFeed)request.Feed!.Value, region, request.Page, ct),
                    "tmdb.recommendations" => await _tmdb.ReadRecommendationsAsync(connection, Kind(request.MediaType), request.Id!.Value, ct),
                    "tmdb.work" => await _tmdb.ReadWorkAsync(connection, Kind(request.MediaType), request.Id!.Value, ct),
                    _ => throw new InvalidOperationException("Unknown TMDB snapshot request.")
                };
            }
            catch (TmdbException exception)
            {
                throw Translate(exception);
            }
        }

        public async Task<long> ImportAsync(LogicContext context, TmdbConnection connection, MediaType mediaType, MediaSubtype? subtype, int tmdbId, CancellationToken ct, bool forceRefresh = false)
        {
            // Read everything before the short write batch.
            TmdbWork work;
            var units = new List<object>();
            var people = new List<object>();
            try
            {
                work = await ReadWorkAsync(context, connection, mediaType, tmdbId, ct, forceRefresh);
                foreach (var season in work.Seasons)
                {
                    units.Add(new { Season = season.SeasonNumber, Episode = (int?)null, Title = TextLimit.Cut(season.Name, 500), ReleasedOn = season.AirDate, Overview = (string?)null,
                        ImageUrl = TmdbClient.PosterUrl(season.PosterPath), RuntimeMinutes = (int?)null });
                    foreach (var episode in await _tmdb.ReadSeasonAsync(connection, tmdbId, season.SeasonNumber, ct))
                    {
                        units.Add(new { Season = season.SeasonNumber, Episode = (int?)episode.EpisodeNumber, Title = TextLimit.Cut(episode.Name, 500), ReleasedOn = episode.AirDate,
                            Overview = TextLimit.Cut(episode.Overview, 4000), ImageUrl = TmdbClient.StillUrl(episode.StillPath), RuntimeMinutes = episode.Runtime is > 0 and <= 1440 ? episode.Runtime : null });
                        people.AddRange(WorkPeopleLogic.Rows(episode.People, WorkUnitType.Episode, season.SeasonNumber, episode.EpisodeNumber));
                    }
                }
            }
            catch (TmdbException exception)
            {
                throw Translate(exception);
            }

            var identities = new List<object> { new { Namespace = Namespace(mediaType), Value = tmdbId.ToString(CultureInfo.InvariantCulture) } };
            if (work.ImdbId is { Length: > 0 and <= 64 } imdb)
            {
                identities.Add(new { Namespace = "imdb", Value = imdb });
            }

            if (work.TvdbId is { } tvdb)
            {
                identities.Add(new { Namespace = "tvdb", Value = tvdb.ToString(CultureInfo.InvariantCulture) });
            }

            // Texts are in the instance language; images, the trailer and studio names are language-neutral.
            var details = work.Details;
            var facts = new[]
            {
                new { Key = "Overview", Value = TextLimit.Cut(work.Overview, 4000), Localized = true },
                new { Key = "Poster", Value = TmdbClient.PosterUrl(work.PosterPath), Localized = false },
                new { Key = "Backdrop", Value = TmdbClient.BackdropUrl(work.BackdropPath), Localized = false },
                new { Key = "Genres", Value = TextLimit.Cut(details.Genres.Count == 0 ? null : string.Join(", ", details.Genres), 4000), Localized = true },
                new { Key = "Trailer", Value = details.TrailerKey, Localized = false },
                new { Key = "Studio", Value = TextLimit.Cut(details.Studios.Count == 0 ? null : string.Join(", ", details.Studios), 4000), Localized = false }
            }.Where(fact => !string.IsNullOrWhiteSpace(fact.Value));
            people.InsertRange(0, WorkPeopleLogic.Rows(details.People));
            // Japanese animation is proposed as Anime unless a subtype was chosen.
            subtype ??= AniListMatching.IsAnime(work.GenreIds, work.OriginalLanguage, work.OriginCountries) ? MediaSubtype.Anime : null;
            var data = new WorkMetadataData(mediaType, subtype, Namespace(mediaType), tmdbId.ToString(CultureInfo.InvariantCulture), TextLimit.Cut(work.Title, 500)!,
                work.OriginalTitle == work.Title ? null : TextLimit.Cut(work.OriginalTitle, 500), work.Year is >= 1800 and <= 2200 ? work.Year : null,
                JsonSerializer.Serialize(identities), JsonSerializer.Serialize(units), JsonSerializer.Serialize(facts), "tmdb", work.ReleasedOn)
            {
                Translations = JsonSerializer.Serialize(work.Translations.Select(translation => new { translation.Language, translation.Region, translation.Title, translation.Overview })),
                OriginalLanguage = work.OriginalLanguage is { Length: 2 } original && original.All(char.IsAsciiLetterLower) ? original : null
            };
            return await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                var workId = await ctx.SaveWorkMetadataAsync(data, token);
                await ctx.SaveWorkFranchiseAsync(new WorkFranchiseData(workId, work.CollectionId is null ? null : "tmdb", work.CollectionId?.ToString(CultureInfo.InvariantCulture),
                    TextLimit.Cut(work.CollectionName, 300), null), token);
                await ctx.SaveWorkRuntimeAsync(new WorkRuntimeData(workId, mediaType == MediaType.Movie ? details.Runtime : null), token);
                await ctx.SaveWorkPeopleAsync(WorkPeopleLogic.Data(workId, "tmdb", people), token);
                return workId;
            }, ct);
        }

        private static TmdbMediaKind Kind(MediaType mediaType) => mediaType switch
        {
            MediaType.Movie => TmdbMediaKind.Movie,
            MediaType.Series => TmdbMediaKind.Series,
            _ => throw new DataValidationException("MediaTypeId", "unknown_catalog_value")
        };

        private static Exception Translate(TmdbException exception) => exception.Failure switch
        {
            TmdbFailure.NotFound => new KeyNotFoundException("TMDB does not know this work."),
            TmdbFailure.AuthenticationFailed => new ServiceUnavailableException("metadata_authentication_failed"),
            TmdbFailure.RateLimited => new ServiceUnavailableException("metadata_rate_limited"),
            _ => new ServiceUnavailableException("metadata_unavailable")
        };
    }
}
