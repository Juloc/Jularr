using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.User.Discover.V1
{
    // One page of a scope's titles as Discover's filters narrow them, in the chosen order (popularity when none).
    // Genres and tags must all match; formats, statuses, sources and sites any one; ranges include their ends.
    public sealed class ReadDiscoverFilteredDataV1 : BaseData
    {
        public ReadDiscoverFilteredDataV1(string scopeSlug, int page = 1, DiscoverSort? discoverSortId = null, Genre[]? genreIds = null, int[]? tagIds = null,
            CatalogFormat[]? catalogFormatIds = null, ReleaseStatus[]? releaseStatusIds = null, CatalogSource[]? catalogSourceIds = null, int[]? siteIds = null, string? countryCode = null,
            string? languageCode = null, int? season = null, int? yearFrom = null, int? yearTo = null, int? episodesFrom = null, int? episodesTo = null, int? chaptersFrom = null,
            int? chaptersTo = null, int? volumesFrom = null, int? volumesTo = null, int? durationFrom = null, int? durationTo = null, bool includesAdult = false)
        {
            ScopeSlug = scopeSlug;
            Page = page;
            DiscoverSortId = discoverSortId;
            GenreIds = genreIds ?? [];
            TagIds = tagIds ?? [];
            CatalogFormatIds = catalogFormatIds ?? [];
            ReleaseStatusIds = releaseStatusIds ?? [];
            CatalogSourceIds = catalogSourceIds ?? [];
            SiteIds = siteIds ?? [];
            CountryCode = countryCode;
            LanguageCode = languageCode;
            Season = season;
            YearFrom = yearFrom;
            YearTo = yearTo;
            EpisodesFrom = episodesFrom;
            EpisodesTo = episodesTo;
            ChaptersFrom = chaptersFrom;
            ChaptersTo = chaptersTo;
            VolumesFrom = volumesFrom;
            VolumesTo = volumesTo;
            DurationFrom = durationFrom;
            DurationTo = durationTo;
            IncludesAdult = includesAdult;
        }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.ScopeSlug))]
        public string ScopeSlug { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.Page))]
        public int Page { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.DiscoverSortId), false)]
        public DiscoverSort? DiscoverSortId { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.GenreIds))]
        public Genre[] GenreIds { get; }

        // AniList's tag ids.
        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.TagIds))]
        public int[] TagIds { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.CatalogFormatIds))]
        public CatalogFormat[] CatalogFormatIds { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.ReleaseStatusIds))]
        public ReleaseStatus[] ReleaseStatusIds { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.CatalogSourceIds))]
        public CatalogSource[] CatalogSourceIds { get; }

        // AniList's streaming or reading sites, or TMDB's streaming services in the instance's country.
        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.SiteIds))]
        public int[] SiteIds { get; }

        // ISO 3166-1 alpha-2 of the country of origin.
        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.CountryCode))]
        public string? CountryCode { get; }

        // The book's language as Open Library names it (MARC, such as ger).
        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.LanguageCode))]
        public string? LanguageCode { get; }

        // 1 winter, 2 spring, 3 summer, 4 fall.
        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.Season))]
        public int? Season { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.Year))]
        public int? YearFrom { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.Year))]
        public int? YearTo { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.Count))]
        public int? EpisodesFrom { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.Count))]
        public int? EpisodesTo { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.Count))]
        public int? ChaptersFrom { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.Count))]
        public int? ChaptersTo { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.Count))]
        public int? VolumesFrom { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.Count))]
        public int? VolumesTo { get; }

        // Minutes of a film or of one episode.
        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.Count))]
        public int? DurationFrom { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.Count))]
        public int? DurationTo { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.IncludesAdult))]
        public bool IncludesAdult { get; }
    }

    // A page of filtered titles: films and series as TMDB's cards, the other types as their catalog's.
    public sealed class DiscoverFilteredV1 : BaseResponse
    {
        public DiscoverFilteredV1(BaseDataList<DiscoverItemV1> items, BaseDataList<DiscoverCatalogItemV1> catalogItems)
        {
            Items = items;
            CatalogItems = catalogItems;
        }

        public BaseDataList<DiscoverItemV1> Items { get; }
        public BaseDataList<DiscoverCatalogItemV1> CatalogItems { get; }
    }

    public sealed class ReadDiscoverFilterOptionsDataV1 : BaseData
    {
        public ReadDiscoverFilterOptionsDataV1(string scopeSlug, bool includesNames = true)
        {
            ScopeSlug = scopeSlug;
            IncludesNames = includesNames;
        }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.ScopeSlug))]
        public string ScopeSlug { get; }

        // Whether the provider is asked for its tags and sites; several types share only genre and year.
        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.IncludesNames))]
        public bool IncludesNames { get; }
    }

    // What a scope's filter offers: its filters, the values of each as its catalog names them, its orders, and the provider's names of AniList's tags
    // and of the sites or services to watch or read on (empty while the provider cannot answer).
    public sealed class DiscoverFilterOptionsV1 : BaseResponse
    {
        public DiscoverFilterOptionsV1(DiscoverFilter[] filters, Genre[] genreIds, CatalogFormat[] catalogFormatIds, ReleaseStatus[] releaseStatusIds, CatalogSource[] catalogSourceIds,
            string[] countryCodes, string[] languageCodes, DiscoverSort[] sorts, BaseDataList<DiscoverOptionV1> tags, BaseDataList<DiscoverOptionV1> sites)
        {
            Filters = filters;
            GenreIds = genreIds;
            CatalogFormatIds = catalogFormatIds;
            ReleaseStatusIds = releaseStatusIds;
            CatalogSourceIds = catalogSourceIds;
            CountryCodes = countryCodes;
            LanguageCodes = languageCodes;
            Sorts = sorts;
            Tags = tags;
            Sites = sites;
        }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.Filters))]
        public DiscoverFilter[] Filters { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.GenreIds))]
        public Genre[] GenreIds { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.CatalogFormatIds))]
        public CatalogFormat[] CatalogFormatIds { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.ReleaseStatusIds))]
        public ReleaseStatus[] ReleaseStatusIds { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.CatalogSourceIds))]
        public CatalogSource[] CatalogSourceIds { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.CountryCodes))]
        public string[] CountryCodes { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.LanguageCodes))]
        public string[] LanguageCodes { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.DiscoverSortIds))]
        public DiscoverSort[] Sorts { get; }

        public BaseDataList<DiscoverOptionV1> Tags { get; }
        public BaseDataList<DiscoverOptionV1> Sites { get; }
    }

    public sealed class DiscoverOptionV1 : BaseResponse
    {
        public DiscoverOptionV1(int id, string name, string? category, bool isAdult)
        {
            Id = id;
            Name = name;
            Category = category;
            IsAdult = isAdult;
        }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.OptionId))]
        public int Id { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.OptionName))]
        public string Name { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.OptionCategory))]
        public string? Category { get; }

        [DataField(typeof(DiscoverFields), nameof(DiscoverFields.IncludesAdult))]
        public bool IsAdult { get; }
    }
}
