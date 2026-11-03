using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Integrations.Data.AniList;
using Jularr.Integrations.Data.Tmdb;

namespace Jularr.Backend.Logic.Media
{
    // What Discover's filter offers each scope, as its catalog can answer it, and how a choice becomes the provider's query.
    internal static class DiscoverFilterScopes
    {
        private static readonly Genre[] AniListGenres = [.. CatalogFactValues.AniListGenres.Values.Order()];

        private static readonly Genre[] MovieGenres =
        [
            Genre.Action, Genre.Adventure, Genre.Animation, Genre.Comedy, Genre.Crime, Genre.Documentary, Genre.Drama, Genre.Family, Genre.Fantasy, Genre.History, Genre.Horror,
            Genre.Music, Genre.Mystery, Genre.Romance, Genre.ScienceFiction, Genre.TvMovie, Genre.Thriller, Genre.War, Genre.Western
        ];

        private static readonly Genre[] SeriesGenres =
        [
            Genre.Action, Genre.Adventure, Genre.Animation, Genre.Comedy, Genre.Crime, Genre.Documentary, Genre.Drama, Genre.Family, Genre.Fantasy, Genre.Kids, Genre.Mystery,
            Genre.News, Genre.Politics, Genre.Reality, Genre.ScienceFiction, Genre.Soap, Genre.Talk, Genre.War, Genre.Western
        ];

        // Open Library's subjects for the genres a book has.
        private static readonly IReadOnlyDictionary<Genre, string> BookSubjects = new Dictionary<Genre, string>
        {
            [Genre.Adventure] = "adventure",
            [Genre.Comedy] = "humor",
            [Genre.Crime] = "crime",
            [Genre.Drama] = "drama",
            [Genre.Fantasy] = "fantasy",
            [Genre.History] = "history",
            [Genre.Horror] = "horror",
            [Genre.Kids] = "juvenile fiction",
            [Genre.Mystery] = "mystery",
            [Genre.Romance] = "romance",
            [Genre.ScienceFiction] = "science fiction",
            [Genre.Thriller] = "thriller"
        };

        // TMDB's series genres that pair two of ours.
        private static readonly IReadOnlyDictionary<Genre, int> SeriesPairs = new Dictionary<Genre, int>
        {
            [Genre.Action] = 10759,
            [Genre.Adventure] = 10759,
            [Genre.ScienceFiction] = 10765,
            [Genre.Fantasy] = 10765,
            [Genre.War] = 10768,
            [Genre.Politics] = 10768
        };

        private static readonly ReleaseStatus[] AniListStatuses = Enum.GetValues<ReleaseStatus>();
        private static readonly ReleaseStatus[] SeriesStatuses = [ReleaseStatus.Finished, ReleaseStatus.Releasing, ReleaseStatus.NotYetReleased, ReleaseStatus.Cancelled];
        private static readonly string[] AniListCountries = ["JP", "KR", "CN", "TW"];
        private static readonly string[] TmdbCountries = ["US", "GB", "DE", "FR", "ES", "IT", "JP", "KR", "CN", "IN"];
        private static readonly string[] BookLanguages = ["ger", "eng", "fre", "spa", "ita", "jpn"];
        private static readonly DiscoverSort[] TmdbSorts = [DiscoverSort.Popularity, DiscoverSort.Score, DiscoverSort.Released, DiscoverSort.Title];
        private static readonly DiscoverSort[] BookSorts = [DiscoverSort.Popularity, DiscoverSort.Released];

        // A scope Discover's filter cannot narrow (music, games) offers nothing.
        public static DiscoverFilterOptionsV1? Options(DiscoverScopeV1 scope, BaseDataList<DiscoverOptionV1> tags, BaseDataList<DiscoverOptionV1> sites) => (scope.MediaTypeId, scope.MediaSubtypeId) switch
        {
            (_, MediaSubtype.Anime) => new(
                [DiscoverFilter.Genre, DiscoverFilter.Year, DiscoverFilter.Season, DiscoverFilter.Format, DiscoverFilter.Status, DiscoverFilter.Country, DiscoverFilter.Source,
                    DiscoverFilter.Tag, DiscoverFilter.Site, DiscoverFilter.Episodes, DiscoverFilter.Duration, DiscoverFilter.Adult],
                AniListGenres, [CatalogFormat.Tv, CatalogFormat.TvShort, CatalogFormat.Movie, CatalogFormat.Special, CatalogFormat.Ova, CatalogFormat.Ona, CatalogFormat.Music],
                AniListStatuses, Enum.GetValues<CatalogSource>(), AniListCountries, [], [.. DiscoverScopes.AniListSorts], tags, sites),
            (_, MediaSubtype.Manga) => new(
                [DiscoverFilter.Genre, DiscoverFilter.Year, DiscoverFilter.Format, DiscoverFilter.Status, DiscoverFilter.Country, DiscoverFilter.Source, DiscoverFilter.Tag,
                    DiscoverFilter.Site, DiscoverFilter.Chapters, DiscoverFilter.Volumes, DiscoverFilter.Adult],
                AniListGenres, [CatalogFormat.Manga, CatalogFormat.OneShot], AniListStatuses, Enum.GetValues<CatalogSource>(), AniListCountries, [], [.. DiscoverScopes.AniListSorts], tags, sites),
            (_, MediaSubtype.LightNovel) => new(
                [DiscoverFilter.Genre, DiscoverFilter.Year, DiscoverFilter.Status, DiscoverFilter.Country, DiscoverFilter.Source, DiscoverFilter.Tag, DiscoverFilter.Site,
                    DiscoverFilter.Volumes, DiscoverFilter.Adult],
                AniListGenres, [], AniListStatuses, Enum.GetValues<CatalogSource>(), AniListCountries, [], [.. DiscoverScopes.AniListSorts], tags, sites),
            (MediaType.Movie, null) => new([DiscoverFilter.Genre, DiscoverFilter.Year, DiscoverFilter.Country, DiscoverFilter.Site, DiscoverFilter.Duration],
                MovieGenres, [], [], [], TmdbCountries, [], TmdbSorts, new([]), sites),
            (MediaType.Series, null) => new([DiscoverFilter.Genre, DiscoverFilter.Year, DiscoverFilter.Status, DiscoverFilter.Country, DiscoverFilter.Site],
                SeriesGenres, [], SeriesStatuses, [], TmdbCountries, [], TmdbSorts, new([]), sites),
            (MediaType.Book, null) => new([DiscoverFilter.Genre, DiscoverFilter.Year, DiscoverFilter.Language],
                [.. BookSubjects.Keys.Order()], [], [], [], [], BookLanguages, BookSorts, new([]), new([])),
            _ => null
        };

        // The whole catalog's shelf, narrowed and sorted; anime's films and series are one scope.
        public static ExternalWorkShelfData Shelf(DiscoverScopeV1 scope, ReadDiscoverFilteredDataV1 data, DiscoverSort sort) =>
            new(0, DiscoverScopes.Namespace(scope), scope.MediaTypeId, scope.MediaSubtypeId, null, sort)
            {
                MediaTypeId = scope.MediaSubtypeId == MediaSubtype.Anime ? (short)0 : (short)scope.MediaTypeId,
                Genres = data.GenreIds.Length > 0 ? [.. data.GenreIds.Select(genre => (short)genre)] : null,
                Tags = data.TagIds.Length > 0 ? data.TagIds : null,
                Formats = data.CatalogFormatIds.Length > 0 ? [.. data.CatalogFormatIds.Select(format => (short)format)] : null,
                Statuses = data.ReleaseStatusIds.Length > 0 ? [.. data.ReleaseStatusIds.Select(status => (short)status)] : null,
                Sources = data.CatalogSourceIds.Length > 0 ? [.. data.CatalogSourceIds.Select(source => (short)source)] : null,
                Sites = data.SiteIds.Length > 0 ? data.SiteIds : null,
                Country = data.CountryCode,
                Season = data.Season is { } season ? (short)season : null,
                YearFrom = data.YearFrom,
                YearTo = data.YearTo,
                EpisodesFrom = data.EpisodesFrom,
                EpisodesTo = data.EpisodesTo,
                ChaptersFrom = data.ChaptersFrom,
                ChaptersTo = data.ChaptersTo,
                VolumesFrom = data.VolumesFrom,
                VolumesTo = data.VolumesTo,
                DurationFrom = data.DurationFrom,
                DurationTo = data.DurationTo,
                Adult = data.IncludesAdult
            };

        // AniList's query of the same choice; a season with one year asks for that season's year, tags go by name.
        public static AniListListQuery AniListQuery(ReadDiscoverFilteredDataV1 data, DiscoverSort sort, IReadOnlyDictionary<int, string> tagNames)
        {
            var seasonYear = data.Season is not null && data.YearFrom == data.YearTo ? data.YearFrom : null;
            return new AniListListQuery(AniListSort(sort), data.Season is { } season ? DiscoverScopes.SeasonName((short)season) : null, seasonYear, data.CountryCode)
            {
                Genres = [.. data.GenreIds.Select(genre => CatalogFactValues.AniListGenres.First(entry => entry.Value == genre).Key)],
                Tags = [.. data.TagIds.Select(id => tagNames.TryGetValue(id, out var name) ? name : null).OfType<string>()],
                Statuses = [.. data.ReleaseStatusIds.Select(status => CatalogFactValues.AniListStatuses.First(entry => entry.Value == status).Key)],
                Sources = [.. data.CatalogSourceIds.Select(source => CatalogFactValues.AniListSources.First(entry => entry.Value == source).Key)],
                SiteIds = data.SiteIds,
                YearFrom = seasonYear is null ? data.YearFrom : null,
                YearTo = seasonYear is null ? data.YearTo : null,
                Episodes = new AniListRange(data.EpisodesFrom, data.EpisodesTo),
                Chapters = new AniListRange(data.ChaptersFrom, data.ChaptersTo),
                Volumes = new AniListRange(data.VolumesFrom, data.VolumesTo),
                Duration = new AniListRange(data.DurationFrom, data.DurationTo),
                IncludesAdult = data.IncludesAdult
            };
        }

        // AniList's format names of the choice, or every format of the scope.
        public static IReadOnlyCollection<string> AniListFormats(DiscoverFilterOptionsV1 options, ReadDiscoverFilteredDataV1 data, MediaSubtype subtype) =>
            [.. (data.CatalogFormatIds.Length > 0 ? data.CatalogFormatIds : subtype == MediaSubtype.LightNovel ? [CatalogFormat.Novel] : options.CatalogFormatIds)
                .Select(format => CatalogFactValues.AniListFormats.First(entry => entry.Value == format).Key)];

        // TMDB's query of the same choice; a genre TMDB pairs with another (series' Action & Adventure) asks for the pair.
        public static TmdbDiscoverQuery TmdbQuery(ReadDiscoverFilteredDataV1 data, DiscoverSort sort, bool series, string? region) => new()
        {
            Sort = sort switch
            {
                DiscoverSort.Score => TmdbDiscoverSort.Rating,
                DiscoverSort.Released => TmdbDiscoverSort.Released,
                DiscoverSort.Title => TmdbDiscoverSort.Title,
                _ => TmdbDiscoverSort.Popularity
            },
            GenreIds = [.. data.GenreIds.Select(genre => series && SeriesPairs.TryGetValue(genre, out var pair) ? pair
                : CatalogFactValues.TmdbGenres.First(entry => entry.Value is [var only] && only == genre).Key).Distinct()],
            YearFrom = data.YearFrom,
            YearTo = data.YearTo,
            RuntimeFrom = data.DurationFrom,
            RuntimeTo = data.DurationTo,
            Country = data.CountryCode,
            ProviderIds = data.SiteIds,
            Region = region,
            SeriesStatuses = [.. data.ReleaseStatusIds.SelectMany(status => status switch
            {
                ReleaseStatus.Finished => new[] { 3 },
                ReleaseStatus.Releasing => [0],
                ReleaseStatus.NotYetReleased => [1, 2, 5],
                ReleaseStatus.Cancelled => [4],
                _ => []
            })]
        };

        public static IReadOnlyList<string> BookSubjectsOf(IEnumerable<Genre> genres) => [.. genres.Select(genre => BookSubjects.TryGetValue(genre, out var subject) ? subject : null).OfType<string>()];

        private static AniListSort AniListSort(DiscoverSort sort) => sort switch
        {
            DiscoverSort.Trending => Integrations.Data.AniList.AniListSort.Trending,
            DiscoverSort.Score => Integrations.Data.AniList.AniListSort.Score,
            DiscoverSort.Favourites => Integrations.Data.AniList.AniListSort.Favourites,
            DiscoverSort.Added => Integrations.Data.AniList.AniListSort.Added,
            DiscoverSort.Released => Integrations.Data.AniList.AniListSort.StartDate,
            DiscoverSort.Title => Integrations.Data.AniList.AniListSort.Title,
            _ => Integrations.Data.AniList.AniListSort.Popularity
        };
    }
}
