using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Data.Fields.Media
{
    public static class DiscoverFields
    {
        public static readonly EnumField DiscoverFeedTypeId = new(typeof(DiscoverFeedType));
        public static readonly EnumArrayField DiscoverFeedTypeIds = new(typeof(DiscoverFeedType)) { MaximumLength = 5 };
        public static readonly EnumField DiscoverSortId = new(typeof(DiscoverSort));
        public static readonly EnumArrayField DiscoverSortIds = new(typeof(DiscoverSort)) { MaximumLength = 7 };
        public static readonly StringField ScopeSlug = new() { MaximumLength = 32, AllowBlank = false };
        public static readonly BoolField IsCatalog = new();
        public static readonly EnumField MediaStateTypeId = new(typeof(MediaStateType));
        public static readonly EnumField LibrarySortTypeId = new(typeof(LibrarySortType));
        public static readonly IntField Page = new() { Minimum = 1, Maximum = 500 };

        // Discover's filters; an empty list or a missing value narrows nothing.
        public static readonly EnumArrayField GenreIds = new(typeof(Genre)) { MaximumLength = 33 };
        public static readonly IntArrayField TagIds = new() { Minimum = 1, MaximumLength = 20, IsSet = true };
        public static readonly EnumArrayField CatalogFormatIds = new(typeof(CatalogFormat)) { MaximumLength = 10 };
        public static readonly EnumArrayField ReleaseStatusIds = new(typeof(ReleaseStatus)) { MaximumLength = 5 };
        public static readonly EnumArrayField CatalogSourceIds = new(typeof(CatalogSource)) { MaximumLength = 15 };
        public static readonly IntArrayField SiteIds = new() { Minimum = 1, MaximumLength = 50, IsSet = true };
        public static readonly StringField CountryCode = new() { MaximumLength = 2, MinimumLength = 2, Required = false, AllowBlank = false };
        public static readonly StringField LanguageCode = new() { MaximumLength = 3, MinimumLength = 3, Required = false, AllowBlank = false };
        public static readonly IntField Season = new() { Minimum = 1, Maximum = 4, Required = false };
        public static readonly IntField Year = new() { Minimum = 1800, Maximum = 2200, Required = false };
        public static readonly IntField Count = new() { Minimum = 0, Maximum = 100000, Required = false };
        public static readonly BoolField IncludesAdult = new();
        public static readonly BoolField IncludesNames = new();
        public static readonly IntField OptionId = new() { Minimum = 1 };
        public static readonly StringField OptionName = new() { MaximumLength = 200, AllowBlank = false };
        public static readonly StringField OptionCategory = new() { MaximumLength = 100, Required = false };
        public static readonly EnumArrayField Filters = new(typeof(DiscoverFilter)) { MaximumLength = 15 };
        public static readonly StringArrayField CountryCodes = new() { ItemMaximumLength = 2, MaximumLength = 20 };
        public static readonly StringArrayField LanguageCodes = new() { ItemMaximumLength = 3, MaximumLength = 20 };
        public static readonly LongField PositionMs = new() { Minimum = 0 };
        public static readonly LongField DurationMs = new() { Minimum = 0, Required = false };
    }
}
