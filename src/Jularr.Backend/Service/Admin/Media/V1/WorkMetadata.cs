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
using Microsoft.Extensions.Caching.Memory;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    // TMDB lookups and imports; shared by Admin catalog work and the System refresh.
    internal sealed class WorkMetadata
    {
        private readonly TmdbClient _tmdb;
        private static readonly TimeSpan FeedLifetime = TimeSpan.FromMinutes(3);
        private readonly IDataProtector _protector;
        private readonly IMemoryCache _cache;

        public WorkMetadata(TmdbClient tmdb, IDataProtectionProvider protection, IMemoryCache cache)
        {
            _tmdb = tmdb;
            _cache = cache;
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

        public async Task<IReadOnlyList<TmdbSearchResult>> SearchAsync(TmdbConnection connection, MediaType mediaType, string query, int? year, CancellationToken ct, int page = 1)
        {
            try
            {
                return await _tmdb.SearchAsync(connection, Kind(mediaType), query, year, ct, page);
            }
            catch (TmdbException exception)
            {
                throw Translate(exception);
            }
        }

        public async Task<IReadOnlyList<TmdbSearchResult>> ReadFeedAsync(TmdbConnection connection, MediaType mediaType, DiscoverFeedType feed, int page, CancellationToken ct)
        {
            // Ranked lists change slowly; a short shared cache spares the provider.
            var region = connection.Language.Split('-') is [_, { Length: 2 } country] ? country.ToUpperInvariant() : null;
            var key = $"metadata.feed.{mediaType}.{feed}.{page}.{connection.Language}.{DateTime.UtcNow:yyyy-MM-dd}";
            if (_cache.TryGetValue(key, out IReadOnlyList<TmdbSearchResult>? cached))
            {
                return cached!;
            }

            try
            {
                var results = await _tmdb.ReadFeedAsync(connection, Kind(mediaType), (TmdbFeed)feed, region, page, ct);
                _cache.Set(key, results, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = FeedLifetime, Size = Math.Max(1, results.Count) });
                return results;
            }
            catch (TmdbException exception)
            {
                throw Translate(exception);
            }
        }

        // Similar titles change slowly; a shared cache of half a day spares the provider.
        public async Task<IReadOnlyList<TmdbSearchResult>> ReadRecommendationsAsync(TmdbConnection connection, MediaType mediaType, int tmdbId, CancellationToken ct)
        {
            var key = $"metadata.recommendations.{mediaType}.{tmdbId.ToString(CultureInfo.InvariantCulture)}.{connection.Language}";
            if (_cache.TryGetValue(key, out IReadOnlyList<TmdbSearchResult>? cached))
            {
                return cached!;
            }

            try
            {
                var results = await _tmdb.ReadRecommendationsAsync(connection, Kind(mediaType), tmdbId, ct);
                _cache.Set(key, results, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(12), Size = Math.Max(1, results.Count) });
                return results;
            }
            catch (TmdbException exception)
            {
                throw Translate(exception);
            }
        }

        public async Task<TmdbWork> ReadWorkAsync(TmdbConnection connection, MediaType mediaType, int tmdbId, CancellationToken ct)
        {
            try
            {
                return await _tmdb.ReadWorkAsync(connection, Kind(mediaType), tmdbId, ct);
            }
            catch (TmdbException exception)
            {
                throw Translate(exception);
            }
        }

        public async Task<long> ImportAsync(LogicContext context, TmdbConnection connection, MediaType mediaType, MediaSubtype? subtype, int tmdbId, CancellationToken ct)
        {
            // Read everything before the short write batch.
            TmdbWork work;
            var units = new List<object>();
            try
            {
                work = await _tmdb.ReadWorkAsync(connection, Kind(mediaType), tmdbId, ct);
                foreach (var season in work.Seasons)
                {
                    units.Add(new { Season = season.SeasonNumber, Episode = (int?)null, Title = TextLimit.Cut(season.Name, 500), ReleasedOn = season.AirDate, Overview = (string?)null,
                        ImageUrl = TmdbClient.PosterUrl(season.PosterPath), RuntimeMinutes = (int?)null });
                    foreach (var episode in await _tmdb.ReadSeasonAsync(connection, tmdbId, season.SeasonNumber, ct))
                    {
                        units.Add(new { Season = season.SeasonNumber, Episode = (int?)episode.EpisodeNumber, Title = TextLimit.Cut(episode.Name, 500), ReleasedOn = episode.AirDate,
                            Overview = TextLimit.Cut(episode.Overview, 4000), ImageUrl = TmdbClient.StillUrl(episode.StillPath), RuntimeMinutes = episode.Runtime is > 0 and <= 1440 ? episode.Runtime : null });
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
            var credits = details.Cast.Select((person, index) => (Person: person, IsCrew: false, Position: index + 1))
                .Concat(details.Crew.Select((person, index) => (Person: person, IsCrew: true, Position: index + 1)))
                .Select(credit => new { credit.IsCrew, credit.Position, Name = TextLimit.Cut(credit.Person.Name, 300), Role = TextLimit.Cut(credit.Person.Role, 300),
                    ImageUrl = TmdbClient.ProfileUrl(credit.Person.ProfilePath) });
            // Japanese animation is proposed as Anime unless a subtype was chosen.
            subtype ??= AniListMatching.IsAnime(work.GenreIds, work.OriginalLanguage, work.OriginCountries) ? MediaSubtype.Anime : null;
            var data = new WorkMetadataData(mediaType, subtype, Namespace(mediaType), tmdbId.ToString(CultureInfo.InvariantCulture), TextLimit.Cut(work.Title, 500)!,
                work.OriginalTitle == work.Title ? null : TextLimit.Cut(work.OriginalTitle, 500), work.Year is >= 1800 and <= 2200 ? work.Year : null,
                JsonSerializer.Serialize(identities), JsonSerializer.Serialize(units), JsonSerializer.Serialize(facts), "tmdb", work.ReleasedOn);
            return await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                var workId = await ctx.SaveWorkMetadataAsync(data, token);
                await ctx.SaveWorkFranchiseAsync(new WorkFranchiseData(workId, work.CollectionId, TextLimit.Cut(work.CollectionName, 300)), token);
                await ctx.SaveWorkCreditsAsync(new WorkCreditsData(workId, mediaType == MediaType.Movie ? details.Runtime : null, JsonSerializer.Serialize(credits)), token);
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
