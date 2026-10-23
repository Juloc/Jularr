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

        // A provider that just failed is not asked again for this long, so a page of shelves does not wait for it on every visit.
        private static readonly TimeSpan FailureLifetime = TimeSpan.FromSeconds(60);
        private readonly IDataProtector _protector;
        private readonly IMemoryCache _cache;
        private readonly CoverStore _covers;
        private readonly TvdbMetadata _tvdb;

        public WorkMetadata(TmdbClient tmdb, IDataProtectionProvider protection, IMemoryCache cache, CoverStore covers, TvdbMetadata tvdb)
        {
            _tmdb = tmdb;
            _cache = cache;
            _covers = covers;
            _tvdb = tvdb;
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

            var failedKey = "metadata.feed.failed." + connection.Language;
            if (_cache.TryGetValue(failedKey, out string? failure))
            {
                throw new ServiceUnavailableException(failure!);
            }

            try
            {
                var results = await _tmdb.ReadFeedAsync(connection, Kind(mediaType), (TmdbFeed)feed, region, page, ct);
                _cache.Set(key, results, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = FeedLifetime, Size = Math.Max(1, results.Count) });
                return results;
            }
            catch (TmdbException exception)
            {
                var translated = Translate(exception);
                if (translated is ServiceUnavailableException unavailable) _cache.Set(failedKey, unavailable.Code, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = FailureLifetime, Size = 1 });
                throw translated;
            }
        }

        // Positive and negative identity lookups share one cache across scans.
        public async Task<int?> FindExternalAsync(TmdbConnection connection, MediaType mediaType, string @namespace, string externalId, CancellationToken ct)
        {
            var key = $"metadata.identity.{mediaType}.{@namespace}.{externalId}";
            if (_cache.TryGetValue(key, out int? cached))
            {
                return cached;
            }

            try
            {
                var result = await _tmdb.FindExternalAsync(connection, Kind(mediaType), @namespace, externalId, ct);
                _cache.Set<int?>(key, result, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = result is null ? TimeSpan.FromHours(12) : TimeSpan.FromDays(7),
                    Size = 1
                });
                return result;
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

        // preferredTvdbId is the series' own TheTVDB id where a person or Sonarr named it (folder, nfo); TMDB's link to TheTVDB is sometimes stale
        // and must not decide which TheTVDB series gives the structure or which id the Work stores.
        public async Task<long> ImportAsync(LogicContext context, TmdbConnection connection, MediaType mediaType, MediaSubtype? subtype, int tmdbId, CancellationToken ct, int? preferredTvdbId = null)
        {
            // Read everything before the short write batch.
            TmdbWork work;
            var units = new List<object>();
            var people = new List<object>();
            TvdbStructure? tvdb = null;
            try
            {
                work = await _tmdb.ReadWorkAsync(connection, Kind(mediaType), tmdbId, ct);

                // A series takes its seasons and episodes from TheTVDB only, like Sonarr; where TVDB cannot name them yet the Work is created without and the next scan asks again.
                if (mediaType == MediaType.Series)
                {
                    tvdb = await _tvdb.TryReadStructureAsync(context, preferredTvdbId ?? work.TvdbId, work.ImdbId, ct);
                }

                if (tvdb is not null)
                {
                    foreach (var season in tvdb.Episodes.GroupBy(episode => episode.SeasonNumber).OrderBy(group => group.Key))
                    {
                        units.Add(new { Season = season.Key, Episode = (int?)null, Title = (string?)null, ReleasedOn = season.Min(episode => episode.Aired), Overview = (string?)null,
                            ImageUrl = (string?)null, RuntimeMinutes = (int?)null });
                        foreach (var episode in season.OrderBy(item => item.EpisodeNumber))
                        {
                            units.Add(new { Season = season.Key, Episode = (int?)episode.EpisodeNumber, Title = TextLimit.Cut(episode.Name, 500), ReleasedOn = episode.Aired, Overview = (string?)null,
                                ImageUrl = (string?)null, RuntimeMinutes = episode.Runtime is > 0 and <= 1440 ? episode.Runtime : null });
                        }
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

            if ((preferredTvdbId ?? work.TvdbId ?? tvdb?.TvdbId) is { } tvdbId)
            {
                identities.Add(new { Namespace = "tvdb", Value = tvdbId.ToString(CultureInfo.InvariantCulture) });
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
            var saved = await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                var workId = await ctx.SaveWorkMetadataAsync(data, token);
                await ctx.SaveWorkFranchiseAsync(new WorkFranchiseData(workId, work.CollectionId is null ? null : "tmdb", work.CollectionId?.ToString(CultureInfo.InvariantCulture),
                    TextLimit.Cut(work.CollectionName, 300), null), token);
                await ctx.SaveWorkRuntimeAsync(new WorkRuntimeData(workId, mediaType == MediaType.Movie ? details.Runtime : null), token);
                await ctx.SaveWorkPeopleAsync(WorkPeopleLogic.Data(workId, "tmdb", people), token);
                return workId;
            }, ct);

            // The poster and backdrop are kept locally.
            await _covers.StoreAsync(context, saved, ct);
            return saved;
        }

        // TMDB's daily id list in batches; false while TMDB has not published the day's list.
        public async Task<bool> ReadIdListAsync(MediaType mediaType, DateOnly day, Func<IReadOnlyList<TmdbExportEntry>, Task> batch, CancellationToken ct)
        {
            var entries = new List<TmdbExportEntry>(IdListBatch);
            try
            {
                await foreach (var entry in _tmdb.ReadIdExportAsync(Kind(mediaType), day, ct))
                {
                    entries.Add(entry);
                    if (entries.Count < IdListBatch) continue;
                    await batch(entries);
                    entries = new List<TmdbExportEntry>(IdListBatch);
                }
            }
            catch (TmdbException exception) when (exception.Failure == TmdbFailure.NotFound)
            {
                return false;
            }
            catch (TmdbException exception)
            {
                throw Translate(exception);
            }

            if (entries.Count > 0) await batch(entries);
            return true;
        }

        private const int IdListBatch = 5000;

        public async Task<(IReadOnlyList<int> Ids, int Pages)> ReadChangesAsync(TmdbConnection connection, MediaType mediaType, DateOnly from, DateOnly to, int page, CancellationToken ct)
        {
            try
            {
                return await _tmdb.ReadChangesAsync(connection, Kind(mediaType), from, to, page, ct);
            }
            catch (TmdbException exception)
            {
                throw Translate(exception);
            }
        }

        // One title for the provider catalog; null when TMDB no longer has it.
        public async Task<TmdbCatalogEntry?> ReadCatalogEntryAsync(TmdbConnection connection, MediaType mediaType, int id, CancellationToken ct)
        {
            try
            {
                return await _tmdb.ReadCatalogEntryAsync(connection, Kind(mediaType), id, ct);
            }
            catch (TmdbException exception)
            {
                throw Translate(exception);
            }
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
