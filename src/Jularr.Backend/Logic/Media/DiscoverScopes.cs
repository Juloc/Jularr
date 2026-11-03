using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.User.Discover.V1;

namespace Jularr.Backend.Logic.Media
{
    // The Discover scopes in the order of the media-type bar; films and series come from TMDB, anime and the other types from their own catalogs.
    // Anime, manga and light novels have AniList's lists and orders; the other types sort only where Jularr holds their whole catalog.
    internal static class DiscoverScopes
    {
        public static IReadOnlyList<DiscoverSort> AniListSorts { get; } = Enum.GetValues<DiscoverSort>();

        public static IReadOnlyList<DiscoverSort> LocalSorts { get; } = [DiscoverSort.Popularity, DiscoverSort.Score, DiscoverSort.Released, DiscoverSort.Title];

        public static IReadOnlyList<DiscoverScopeV1> All { get; } =
        [
            Scope("movies", MediaType.Movie, null, DiscoverFeedType.Trending, DiscoverFeedType.Top, DiscoverFeedType.New, DiscoverFeedType.Upcoming),
            Scope("series", MediaType.Series, null, DiscoverFeedType.Trending, DiscoverFeedType.Top, DiscoverFeedType.New),
            Scope("anime", MediaType.Series, MediaSubtype.Anime, DiscoverFeedType.Trending, DiscoverFeedType.Season, DiscoverFeedType.NextSeason, DiscoverFeedType.Popular, DiscoverFeedType.Top100),
            Scope("lightnovels", MediaType.Book, MediaSubtype.LightNovel, DiscoverFeedType.Trending, DiscoverFeedType.Popular, DiscoverFeedType.Top100),
            Scope("books", MediaType.Book, null, DiscoverFeedType.Trending),
            Scope("manga", MediaType.Book, MediaSubtype.Manga, DiscoverFeedType.Trending, DiscoverFeedType.Popular, DiscoverFeedType.Manhwa, DiscoverFeedType.Top100),
            Scope("music", MediaType.Music, null),
            Scope("games", MediaType.Game, null)
        ];

        public static bool IsCatalog(MediaType type, MediaSubtype? subtype) => subtype == MediaSubtype.Anime || type is not (MediaType.Movie or MediaType.Series);

        public static bool IsAniList(MediaSubtype? subtype) => subtype is MediaSubtype.Anime or MediaSubtype.Manga or MediaSubtype.LightNovel;

        public static string Namespace(DiscoverScopeV1 scope) => IsAniList(scope.MediaSubtypeId)
            ? "anilist"
            : scope.MediaTypeId switch
            {
                MediaType.Movie => "tmdb_movie",
                MediaType.Series => "tmdb_tv",
                MediaType.Book => "openlibrary",
                MediaType.Music => "musicbrainz",
                _ => "igdb"
            };

        // A list's own order, as AniList opens it.
        public static DiscoverSort OwnSort(DiscoverFeedType feed) => feed switch
        {
            DiscoverFeedType.Trending => DiscoverSort.Trending,
            DiscoverFeedType.Top or DiscoverFeedType.Top100 => DiscoverSort.Score,
            _ => DiscoverSort.Popularity
        };

        // The hundred best rated are five pages.
        public const int Top100Pages = 5;

        // AniList's seasons by quarter: winter begins the year; the next season may begin the next year.
        public static (short Season, short Year) Season(DateOnly day, int ahead)
        {
            var quarter = (day.Year * 4) + ((day.Month - 1) / 3) + ahead;
            return ((short)((quarter % 4) + 1), (short)(quarter / 4));
        }

        public static string SeasonName(short season) => season switch
        {
            1 => "WINTER",
            2 => "SPRING",
            3 => "SUMMER",
            _ => "FALL"
        };

        public static short? SeasonNumber(string? name) => name switch
        {
            "WINTER" => 1,
            "SPRING" => 2,
            "SUMMER" => 3,
            "FALL" => 4,
            _ => null
        };

        private static DiscoverScopeV1 Scope(string slug, MediaType type, MediaSubtype? subtype, params DiscoverFeedType[] feeds) =>
            new(slug, type, subtype, IsCatalog(type, subtype), feeds, [.. feeds.Select(OwnSort)], IsAniList(subtype) ? [.. AniListSorts] : []);
    }
}
