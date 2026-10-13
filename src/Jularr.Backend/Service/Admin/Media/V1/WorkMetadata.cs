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

        public async Task<IReadOnlyList<TmdbSearchResult>> SearchAsync(TmdbConnection connection, MediaType mediaType, string query, int? year, CancellationToken ct)
        {
            try
            {
                return await _tmdb.SearchAsync(connection, Kind(mediaType), query, year, ct);
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
            var key = $"metadata.feed.{mediaType}.{feed}.{page}.{connection.Language}";
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

        public async Task<long> ImportAsync(LogicContext context, TmdbConnection connection, MediaType mediaType, int tmdbId, CancellationToken ct)
        {
            // Read everything before the short write batch.
            TmdbWork work;
            var units = new List<object>();
            try
            {
                work = await _tmdb.ReadWorkAsync(connection, Kind(mediaType), tmdbId, ct);
                foreach (var season in work.Seasons)
                {
                    units.Add(new { Season = season.SeasonNumber, Episode = (int?)null, Title = Limit(season.Name), ReleasedOn = season.AirDate });
                    foreach (var episode in await _tmdb.ReadSeasonAsync(connection, tmdbId, season.SeasonNumber, ct))
                    {
                        units.Add(new { Season = season.SeasonNumber, Episode = (int?)episode.EpisodeNumber, Title = Limit(episode.Name), ReleasedOn = episode.AirDate });
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

            // Overview is in the instance language; image paths are language-neutral.
            var facts = new[]
            {
                new { Key = "Overview", Value = work.Overview is { Length: > 4000 } overview ? overview[..4000] : work.Overview, Localized = true },
                new { Key = "Poster", Value = TmdbClient.PosterUrl(work.PosterPath), Localized = false },
                new { Key = "Backdrop", Value = TmdbClient.BackdropUrl(work.BackdropPath), Localized = false }
            }.Where(fact => fact.Value is not null);
            var data = new WorkMetadataData(mediaType, Namespace(mediaType), tmdbId.ToString(CultureInfo.InvariantCulture), Limit(work.Title)!,
                work.OriginalTitle == work.Title ? null : Limit(work.OriginalTitle), work.Year is >= 1800 and <= 2200 ? work.Year : null,
                JsonSerializer.Serialize(identities), JsonSerializer.Serialize(units), JsonSerializer.Serialize(facts));
            return await context.ExecuteBatchAsync((ctx, token) => ctx.SaveWorkMetadataAsync(data, token), ct);
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

        private static string? Limit(string? value) => value is null ? null : value.Length <= 500 ? value : value[..500];
    }
}
