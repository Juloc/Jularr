using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Enums.Sql;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Provider;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Logic.Provider;
using Jularr.Backend.Service.Admin.Provider.V1;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Data.Tmdb;
using Jularr.Integrations.Data.Tvdb;
using Jularr.Integrations.Logic.Tmdb;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.Admin.Media.V1
{
    // Film imports from TMDB, series imports and search from TheTVDB with TMDB's rating, and TMDB lookups; shared by Admin catalog work and the System refresh.
    internal sealed class WorkMetadata
    {
        private readonly TmdbClient _tmdb;

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
                (await ctx.ReadProviderCredentialAsync(new ProviderParameters("tmdb"), token), await ReadLocaleAsync(ctx, token)), ct, mode: SqlAccessMode.ReadOnly);

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

        // The instance language metadata is read in, such as "de-DE".
        private static async Task<string> ReadLocaleAsync(LogicContext ctx, CancellationToken ct)
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
            return (await ctx.ReadSql.ReadOptionalAsync<MetadataLanguageData>(sql, ct))?.Locale ?? "en";
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
            var region = connection.Language.Split('-') is [_, { Length: 2 } country] ? country.ToUpperInvariant() : null;
            var failedKey = "metadata.feed.failed." + connection.Language;
            if (_cache.TryGetValue(failedKey, out string? failure))
            {
                throw new ServiceUnavailableException(failure!);
            }

            try
            {
                return await _tmdb.ReadFeedAsync(connection, Kind(mediaType), (TmdbFeed)feed, region, page, ct);
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

        public async Task<IReadOnlyList<TmdbSearchResult>> ReadRecommendationsAsync(TmdbConnection connection, MediaType mediaType, int tmdbId, CancellationToken ct)
        {
            var failedKey = "metadata.feed.failed." + connection.Language;
            if (_cache.TryGetValue(failedKey, out string? failure))
            {
                throw new ServiceUnavailableException(failure!);
            }

            try
            {
                return await _tmdb.ReadRecommendationsAsync(connection, Kind(mediaType), tmdbId, ct);
            }
            catch (TmdbException exception)
            {
                var translated = Translate(exception);
                if (translated is ServiceUnavailableException unavailable) _cache.Set(failedKey, unavailable.Code, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = FailureLifetime, Size = 1 });
                throw translated;
            }
        }

        public async Task<IReadOnlyList<ExternalWorkData>> FetchExternalFeedAsync(LogicContext context, ExternalFeedData feed, CancellationToken ct)
        {
            var connection = await ConnectAsync(context, ct);
            if (feed.Language is { } language)
            {
                connection = new TmdbConnection(connection.Credential, language);
            }
            IReadOnlyList<TmdbSearchResult> results;
            try
            {
                results = feed.SourceValue is { } value
                    ? await ReadRecommendationsAsync(connection, feed.MediaTypeId, int.Parse(value, CultureInfo.InvariantCulture), ct)
                    : await ReadFeedAsync(connection, feed.MediaTypeId, feed.DiscoverFeedTypeId!.Value, feed.Page, ct);
            }
            catch (KeyNotFoundException) when (feed.SourceValue is not null)
            {
                results = [];
            }
            return await ExternalWorksAsync(context, connection, feed.MediaTypeId, results, ct);
        }

        // TMDB's lists name films and series; a series is the TheTVDB series TMDB's id leads to, through the Work that already holds both ids or else TMDB's link,
        // and keeps TMDB's id as its second identity. A series without a TheTVDB id is left out.
        public async Task<IReadOnlyList<ExternalWorkData>> ExternalWorksAsync(LogicContext context, TmdbConnection connection, MediaType mediaType, IReadOnlyList<TmdbSearchResult> results,
            CancellationToken ct)
        {
            if (mediaType == MediaType.Movie)
            {
                return [.. results.Select(result => ExternalWork(Namespace(MediaType.Movie), result))];
            }

            var values = results.Select(result => result.Id.ToString(CultureInfo.InvariantCulture)).ToArray();
            var known = await context.ExecuteBatchAsync((ctx, token) => ReadTvdbLinksAsync(ctx, values, token), ct, mode: SqlAccessMode.ReadOnly);
            var works = new List<ExternalWorkData>();
            foreach (var result in results)
            {
                var tmdbValue = result.Id.ToString(CultureInfo.InvariantCulture);
                var tvdbId = known.TryGetValue(tmdbValue, out var linked) ? linked : null;
                try
                {
                    tvdbId ??= await FindTvdbIdAsync(connection, result.Id, ct);
                }
                catch (KeyNotFoundException)
                {
                    // TMDB no longer knows the entry.
                }

                if (tvdbId is not { } id)
                {
                    continue;
                }

                var entry = ExternalWork("tvdb", result);
                works.Add(new ExternalWorkData("tvdb", id.ToString(CultureInfo.InvariantCulture), entry.Title, entry.OriginalTitle, entry.ReleaseYear, entry.Overview, null, entry.ImageUrl,
                    entry.BackdropUrl, null, entry.Rating) { TmdbValue = tmdbValue });
            }

            return [.. works.DistinctBy(work => work.Value)];
        }

        // The TheTVDB ids Works hold next to these TMDB series ids.
        private static async Task<IReadOnlyDictionary<string, int?>> ReadTvdbLinksAsync(LogicContext ctx, IReadOnlyList<string> tmdbValues, CancellationToken ct)
        {
            if (tmdbValues.Count == 0) return new Dictionary<string, int?>();

            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    tmdb."Value" AS "TmdbValue",
                    tvdb."Value" AS "TvdbValue"
                FROM
                    "WorkExternalIdentities" AS tmdb
                    JOIN jsonb_to_recordset(@Values::jsonb) AS item ("Namespace" text, "Value" text) ON item."Namespace" = tmdb."Namespace" AND item."Value" = tmdb."Value"
                    JOIN "WorkExternalIdentities" AS tvdb ON tvdb."WorkId" = tmdb."WorkId" AND tvdb."Namespace" = 'tvdb';
                """;
            sql.Parameters.Add(new WorkIdentityLookupData(JsonSerializer.Serialize(tmdbValues.Select(value => new { Namespace = "tmdb_tv", Value = value }))));
            return (await ctx.ReadSql.ReadListAsync<SeriesIdentityLinkData>(sql, ct)).ToDictionary(link => link.TmdbValue,
                link => int.TryParse(link.TvdbValue, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : (int?)null, StringComparer.Ordinal);
        }

        // TheTVDB's series search in the instance language, a page of twenty; each result is a title of TheTVDB's catalog.
        public async Task<IReadOnlyList<ExternalWorkData>> SearchSeriesAsync(LogicContext context, string query, int? year, CancellationToken ct, int page = 1)
        {
            var (locale, credential) = await context.ExecuteBatchAsync(async (ctx, token) =>
                (await ReadLocaleAsync(ctx, token), await ctx.ReadProviderCredentialAsync(new ProviderParameters("tvdb"), token)), ct, mode: SqlAccessMode.ReadOnly);
            var language = LanguageCodes.ToThreeLetter(locale) ?? "eng";
            var session = await _tvdb.LoginAsync(credential, ct);
            var results = await _tvdb.SearchSeriesAsync(session, query, year, (Math.Max(page, 1) - 1) * SearchPageSize, SearchPageSize, ct);
            return [.. results.Select(result =>
            {
                string? Text(IReadOnlyDictionary<string, string> texts) => texts.GetValueOrDefault(language) ?? texts.GetValueOrDefault("eng");
                var title = TextLimit.Cut(Text(result.Names) ?? result.Name, 500)!;
                var original = TextLimit.Cut(result.OriginalLanguage is { } code ? result.Names.GetValueOrDefault(code) ?? result.Name : result.Name, 500);
                return new ExternalWorkData("tvdb", result.Id.ToString(CultureInfo.InvariantCulture), title, original == title ? null : original,
                    result.Year is >= 1800 and <= 2200 ? result.Year : null, TextLimit.Cut(Text(result.Overviews) ?? result.Overview, 4000), null, Address(result.ImageUrl), null, null);
            })];
        }

        private const int SearchPageSize = 20;

        // The TheTVDB id of a series another id names: its own, IMDb's through TheTVDB, TMDB's through TMDB's link; null while the catalog knows none.
        public async Task<int?> FindTvdbIdAsync(LogicContext context, string @namespace, string value, CancellationToken ct)
        {
            var number = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : (int?)null;
            try
            {
                switch (@namespace)
                {
                    case "tvdb":
                        return number;
                    case "tmdb_tv" when number is { } tmdbId:
                        return await FindTvdbIdAsync(await ConnectAsync(context, ct), tmdbId, ct);
                    case "imdb":
                        var key = "metadata.identity.tvdb.imdb." + value;
                        if (_cache.TryGetValue(key, out int? cached))
                        {
                            return cached;
                        }

                        var found = await _tvdb.FindSeriesByImdbAsync(await _tvdb.OpenAsync(context, ct), value, ct);
                        _cache.Set(key, found, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = found is null ? TimeSpan.FromHours(12) : TimeSpan.FromDays(7), Size = 1 });
                        return found;
                    default:
                        return null;
                }
            }
            catch (KeyNotFoundException)
            {
                return null;
            }
        }

        public static ExternalWorkData ExternalWork(string @namespace, TmdbSearchResult result) => new(@namespace, result.Id.ToString(CultureInfo.InvariantCulture),
            TextLimit.Cut(result.Title, 500)!, TextLimit.Cut(result.OriginalTitle, 500), result.Year is >= 1800 and <= 2200 ? result.Year : null, TextLimit.Cut(result.Overview, 4000), null,
            TmdbClient.PosterUrl(result.PosterPath), TmdbClient.BackdropUrl(result.BackdropPath), null,
            result.VoteAverage is > 0 and <= 10 ? Math.Round((decimal)result.VoteAverage.Value, 2) : null);

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

        // A film comes from TMDB. A series comes from TheTVDB, found by preferredTvdbId, the series' own id a person or Sonarr named (folder, nfo), or else by
        // TMDB's link to TheTVDB, which is sometimes stale and must not overrule the series' own.
        public async Task<long> ImportAsync(LogicContext context, TmdbConnection connection, MediaType mediaType, MediaSubtype? subtype, int tmdbId, CancellationToken ct, int? preferredTvdbId = null)
        {
            if (mediaType == MediaType.Series)
            {
                var tvdbId = preferredTvdbId ?? await FindTvdbIdAsync(connection, tmdbId, ct) ?? throw new KeyNotFoundException("TheTVDB does not know this series.");
                return await ImportSeriesAsync(context, subtype, tvdbId, tmdbId, ct);
            }

            // Read everything before the short write batch.
            var work = await ReadWorkAsync(connection, mediaType, tmdbId, ct);
            var identities = new List<object> { new { Namespace = Namespace(mediaType), Value = tmdbId.ToString(CultureInfo.InvariantCulture) } };
            if (work.ImdbId is { Length: > 0 and <= 64 } imdb)
            {
                identities.Add(new { Namespace = "imdb", Value = imdb });
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

            // Japanese animation is proposed as Anime unless a subtype was chosen.
            subtype ??= AniListMatching.IsAnime(work.GenreIds, work.OriginalLanguage, work.OriginCountries) ? MediaSubtype.Anime : null;
            var data = new WorkMetadataData(mediaType, subtype, Namespace(mediaType), tmdbId.ToString(CultureInfo.InvariantCulture), TextLimit.Cut(work.Title, 500)!,
                work.OriginalTitle == work.Title ? null : TextLimit.Cut(work.OriginalTitle, 500), work.Year is >= 1800 and <= 2200 ? work.Year : null,
                JsonSerializer.Serialize(identities), "[]", JsonSerializer.Serialize(facts), "tmdb", work.ReleasedOn)
            {
                Translations = JsonSerializer.Serialize(work.Translations.Select(translation => new { translation.Language, translation.Region, translation.Title, translation.Overview })),
                OriginalLanguage = work.OriginalLanguage is { Length: 2 } original && original.All(char.IsAsciiLetterLower) ? original : null
            };
            var saved = await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                var workId = await ctx.SaveWorkMetadataAsync(data, token);
                await ctx.SaveWorkFranchiseAsync(new WorkFranchiseData(workId, work.CollectionId is null ? null : "tmdb", work.CollectionId?.ToString(CultureInfo.InvariantCulture),
                    TextLimit.Cut(work.CollectionName, 300), null), token);
                await ctx.SaveWorkRuntimeAsync(new WorkRuntimeData(workId, details.Runtime), token);
                await ctx.SaveWorkPeopleAsync(WorkPeopleLogic.Data(workId, "tmdb", WorkPeopleLogic.Rows(details.People)), token);
                return workId;
            }, ct);

            // The poster and backdrop are kept locally.
            await _covers.StoreAsync(context, saved, ct);
            return saved;
        }

        // A series a caller knows by its TheTVDB, TMDB or IMDb id; the TMDB and IMDb ids lead to TheTVDB first.
        public async Task<long> ImportSeriesAsync(LogicContext context, MediaSubtype? subtype, string @namespace, string value, CancellationToken ct)
        {
            var tvdbId = await FindTvdbIdAsync(context, @namespace, value, ct) ?? throw new KeyNotFoundException("TheTVDB does not know this series.");
            var tmdbId = @namespace == "tmdb_tv" && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var tmdb) && tmdb > 0 ? tmdb : (int?)null;
            return await ImportSeriesAsync(context, subtype, tvdbId, tmdbId, ct);
        }

        // A series comes from TheTVDB only, like Sonarr: its titles and summaries in every language, artwork, people, genres, networks, trailer, seasons and episodes.
        // TMDB gives only the rating, through the series' TMDB id: the one the caller found it by, else the one the Work holds, else TheTVDB's link; without one,
        // or while TMDB fails, the Work keeps its rating and the import goes on. Without TheTVDB nothing changes.
        public async Task<long> ImportSeriesAsync(LogicContext context, MediaSubtype? subtype, int tvdbId, int? tmdbTvId, CancellationToken ct)
        {
            // Read everything before the short write batch.
            var (locale, stored, credential) = await context.ExecuteBatchAsync(async (ctx, token) =>
                (await ReadLocaleAsync(ctx, token), await ReadSeriesIdentitiesAsync(ctx, tvdbId, token), await ctx.ReadProviderCredentialAsync(new ProviderParameters("tvdb"), token)),
                ct, mode: SqlAccessMode.ReadOnly);
            var language = LanguageCodes.ToThreeLetter(locale) ?? "eng";
            var session = await _tvdb.LoginAsync(credential, ct);
            var series = await _tvdb.ReadSeriesAsync(session, tvdbId, ct);
            var episodes = await _tvdb.ReadEpisodesAsync(session, tvdbId, null, ct);

            // Texts in the instance language, else in English, else in the original; episodes in the original language need no second read.
            var preferred = new[] { language, "eng" }.Distinct(StringComparer.Ordinal).ToArray();
            var original = episodes.ToDictionary(episode => episode.Id);
            var translated = new List<IReadOnlyDictionary<int, TvdbEpisode>>();
            foreach (var code in preferred)
            {
                translated.Add(code == series.OriginalLanguage ? original : (await _tvdb.ReadEpisodesAsync(session, tvdbId, code, ct)).ToDictionary(episode => episode.Id));
            }

            string? Text(Func<TvdbTranslation, string?> value) =>
                preferred.Select(code => series.Translations.FirstOrDefault(translation => translation.Language == code)).Select(translation => translation is null ? null : value(translation))
                    .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
            string? EpisodeText(TvdbEpisode episode, Func<TvdbEpisode, string?> value) =>
                translated.Select(list => list.GetValueOrDefault(episode.Id)).Select(item => item is null ? null : value(item)).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
                    ?? value(episode);

            // An episode's title is always English, like Sonarr, so file names do not depend on the instance language; without one it keeps its original name.
            var englishEpisodes = translated[Array.IndexOf(preferred, "eng")];
            string? EpisodeTitle(TvdbEpisode episode) => englishEpisodes.GetValueOrDefault(episode.Id)?.Name is { } english && !string.IsNullOrWhiteSpace(english) ? english : episode.Name;
            int Rank(string? code) => code == language ? 0 : code == "eng" ? 1 : 2;

            // A season holds its episodes only while TheTVDB numbers each one once; otherwise the Work goes without and the next import asks again.
            var units = new List<object>();
            if (episodes.Count > 0 && episodes.GroupBy(episode => (episode.SeasonNumber, episode.EpisodeNumber)).All(group => group.Count() == 1))
            {
                var seasonImages = series.Seasons.ToDictionary(season => season.Number, season => season.ImageUrl);
                foreach (var season in episodes.GroupBy(episode => episode.SeasonNumber).OrderBy(group => group.Key))
                {
                    units.Add(new { Season = season.Key, Episode = (int?)null, Title = (string?)null, ReleasedOn = season.Min(episode => episode.Aired), Overview = (string?)null,
                        ImageUrl = Address(seasonImages.GetValueOrDefault(season.Key)), RuntimeMinutes = (int?)null });
                    foreach (var episode in season.OrderBy(item => item.EpisodeNumber))
                    {
                        units.Add(new { Season = season.Key, Episode = (int?)episode.EpisodeNumber, Title = TextLimit.Cut(EpisodeTitle(episode), 500), ReleasedOn = episode.Aired,
                            Overview = TextLimit.Cut(EpisodeText(episode, item => item.Overview), 4000), ImageUrl = Address(episode.ImageUrl), RuntimeMinutes = episode.Runtime });
                    }
                }
            }

            // The best artwork of a kind: in the instance language, else in English, else any, the best rated first.
            string? Artwork(TvdbArtworkType type) => series.Artworks.Where(artwork => artwork.Type == type && Address(artwork.ImageUrl) is not null)
                .OrderBy(artwork => Rank(artwork.Language)).ThenByDescending(artwork => artwork.Score).Select(artwork => artwork.ImageUrl).FirstOrDefault();
            var facts = new[]
            {
                new { Key = "Overview", Value = TextLimit.Cut(Text(translation => translation.Overview) ?? series.Overview, 4000), Localized = true },
                new { Key = "Poster", Value = Artwork(TvdbArtworkType.Poster) ?? Address(series.ImageUrl), Localized = false },
                new { Key = "Backdrop", Value = Artwork(TvdbArtworkType.Background), Localized = false },
                new { Key = "Genres", Value = TextLimit.Cut(series.Genres.Count == 0 ? null : string.Join(", ", series.Genres), 4000), Localized = true },
                new { Key = "Trailer", Value = series.Trailers.OrderBy(trailer => Rank(trailer.Language)).Select(trailer => trailer.YouTubeKey).FirstOrDefault(), Localized = false },
                new { Key = "Studio", Value = TextLimit.Cut(series.Networks.Count == 0 ? null : string.Join(", ", series.Networks), 4000), Localized = false }
            }.Where(fact => !string.IsNullOrWhiteSpace(fact.Value));

            // Actors with their characters, then the people behind the series, in TheTVDB's order.
            var people = series.Characters.OrderBy(character => character.Sort).Select(character => (Character: character, Role: character.Role switch
            {
                "Actor" => ProviderPerson.Actor,
                "Guest Star" => ProviderPerson.GuestStar,
                "Director" => ProviderPerson.Director,
                "Writer" => ProviderPerson.Writer,
                "Creator" => ProviderPerson.Creator,
                _ => null
            })).Where(item => item.Role is not null).Select(item => new ProviderPerson(item.Character.PersonId?.ToString(CultureInfo.InvariantCulture), item.Character.PersonName, item.Role!,
                item.Role is ProviderPerson.Actor or ProviderPerson.GuestStar ? item.Character.Character : null, item.Character.ImageUrl));

            // The rating alone still comes from TMDB.
            var tmdbId = tmdbTvId ?? stored.TmdbTvId ?? series.TmdbId;
            TmdbCatalogEntry? rated = null;
            if (tmdbId is { } ratedId)
            {
                try
                {
                    rated = await ReadCatalogEntryAsync(await ConnectAsync(context, ct), MediaType.Series, ratedId, ct);
                }
                catch (ServiceUnavailableException)
                {
                    // Without TMDB the series keeps its rating.
                }
            }

            var identities = new List<object> { new { Namespace = "tvdb", Value = tvdbId.ToString(CultureInfo.InvariantCulture) } };
            if (tmdbId is { } tmdb)
            {
                identities.Add(new { Namespace = "tmdb_tv", Value = tmdb.ToString(CultureInfo.InvariantCulture) });
            }

            if ((stored.ImdbId ?? series.ImdbId) is { Length: > 0 and <= 64 } imdb)
            {
                identities.Add(new { Namespace = "imdb", Value = imdb });
            }

            // Japanese animation and TheTVDB's anime are proposed as Anime unless a subtype was chosen.
            var originalLanguage = LanguageCodes.ToTwoLetter(series.OriginalLanguage);
            subtype ??= AniListMatching.IsAnime(series.Genres, originalLanguage, series.OriginalCountry) ? MediaSubtype.Anime : null;
            var title = TextLimit.Cut(Text(translation => translation.Name) ?? series.Name, 500)!;
            var originalTitle = TextLimit.Cut(series.Translations.FirstOrDefault(translation => translation.Language == series.OriginalLanguage)?.Name ?? series.Name, 500);
            var year = series.FirstAired?.Year ?? series.Year;
            var data = new WorkMetadataData(MediaType.Series, subtype, "tvdb", tvdbId.ToString(CultureInfo.InvariantCulture), title, originalTitle == title ? null : originalTitle,
                year is >= 1800 and <= 2200 ? year : null, JsonSerializer.Serialize(identities), JsonSerializer.Serialize(units), JsonSerializer.Serialize(facts), "tvdb", series.FirstAired)
            {
                Translations = JsonSerializer.Serialize(series.Translations.Select(translation => (Language: LanguageCodes.ToTwoLetter(translation.Language), Translation: translation))
                    .Where(item => item.Language is not null).DistinctBy(item => item.Language)
                    .Select(item => new { item.Language, Region = (string?)null, Title = item.Translation.Name, item.Translation.Overview })),
                OriginalLanguage = originalLanguage is { Length: 2 } two && two.All(char.IsAsciiLetterLower) ? two : null
            };
            var rating = rated?.VoteAverage is > 0 and <= 10 ? Math.Round((decimal)rated.VoteAverage.Value, 2) : (decimal?)null;
            var saved = await context.ExecuteBatchAsync(async (ctx, token) =>
            {
                var workId = await ctx.SaveWorkMetadataAsync(data, token);
                await ctx.SaveWorkFranchiseAsync(new WorkFranchiseData(workId, null, null, null, null), token);
                await ctx.SaveWorkPeopleAsync(WorkPeopleLogic.Data(workId, "tvdb", WorkPeopleLogic.Rows(people)), token);
                await ctx.SaveWorkRatingAsync(new WorkRatingData(workId, rating, rating is null ? null : rated!.VoteCount is >= 0 ? rated.VoteCount : null), token);
                return workId;
            }, ct);

            // The poster and backdrop are kept locally.
            await _covers.StoreAsync(context, saved, ct);
            return saved;
        }

        // The TMDB and IMDb ids of the Work that already holds a TheTVDB series; they stay its own.
        private static async Task<(int? TmdbTvId, string? ImdbId)> ReadSeriesIdentitiesAsync(LogicContext ctx, int tvdbId, CancellationToken ct)
        {
            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    identity."Namespace",
                    identity."Value"
                FROM
                    "WorkExternalIdentities" AS known
                    JOIN "WorkExternalIdentities" AS identity ON identity."WorkId" = known."WorkId" AND identity."Namespace" IN ('tmdb_tv', 'imdb')
                WHERE
                    known."Namespace" = @Namespace
                    AND known."Value" = @Value;
                """;
            sql.Parameters.Add(new WorkIdentityProbeData("tvdb", tvdbId.ToString(CultureInfo.InvariantCulture)));
            var identities = await ctx.ReadSql.ReadListAsync<WorkIdentityProbeData>(sql, ct);
            return (int.TryParse(identities.FirstOrDefault(identity => identity.Namespace == "tmdb_tv")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var tmdb) && tmdb > 0 ? tmdb : null,
                identities.FirstOrDefault(identity => identity.Namespace == "imdb")?.Value);
        }

        // TMDB's link from a series to TheTVDB; positive and negative answers are kept like the other identity lookups.
        private async Task<int?> FindTvdbIdAsync(TmdbConnection connection, int tmdbId, CancellationToken ct)
        {
            var key = "metadata.identity.tvdb." + tmdbId.ToString(CultureInfo.InvariantCulture);
            if (_cache.TryGetValue(key, out int? cached))
            {
                return cached;
            }

            var result = (await ReadCatalogEntryAsync(connection, MediaType.Series, tmdbId, ct) ?? throw new KeyNotFoundException("TMDB does not know this series.")).TvdbId;
            _cache.Set(key, result, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = result is null ? TimeSpan.FromHours(12) : TimeSpan.FromDays(7), Size = 1 });
            return result;
        }

        // Image addresses the catalog may keep.
        private static string? Address(string? url) => url is { Length: <= 500 } && url.StartsWith("https://", StringComparison.Ordinal) ? url : null;

        // TMDB's daily film id list in batches; false while TMDB has not published the day's list.
        public async Task<bool> ReadMovieIdListAsync(DateOnly day, Func<IReadOnlyList<TmdbExportEntry>, Task> batch, CancellationToken ct)
        {
            var entries = new List<TmdbExportEntry>(IdListBatch);
            try
            {
                await foreach (var entry in _tmdb.ReadMovieIdExportAsync(day, ct))
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

        public async Task<(IReadOnlyList<int> Ids, int Pages)> ReadMovieChangesAsync(TmdbConnection connection, DateOnly from, DateOnly to, int page, CancellationToken ct)
        {
            try
            {
                return await _tmdb.ReadMovieChangesAsync(connection, from, to, page, ct);
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
