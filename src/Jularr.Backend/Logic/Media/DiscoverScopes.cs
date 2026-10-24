using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.User.Discover.V1;

namespace Jularr.Backend.Logic.Media
{
    // The Discover scopes in the order of the media-type bar; films and series come from TMDB, anime and the other types from their own catalogs.
    internal static class DiscoverScopes
    {
        public static IReadOnlyList<DiscoverScopeV1> All { get; } =
        [
            Scope("movies", MediaType.Movie, null, DiscoverFeedType.Trending, DiscoverFeedType.Top, DiscoverFeedType.New, DiscoverFeedType.Upcoming),
            Scope("series", MediaType.Series, null, DiscoverFeedType.Trending, DiscoverFeedType.Top, DiscoverFeedType.New),
            Scope("anime", MediaType.Series, MediaSubtype.Anime, DiscoverFeedType.Trending, DiscoverFeedType.Top, DiscoverFeedType.New),
            Scope("lightnovels", MediaType.Book, MediaSubtype.LightNovel, DiscoverFeedType.Trending, DiscoverFeedType.Top, DiscoverFeedType.New),
            Scope("books", MediaType.Book, null, DiscoverFeedType.Trending),
            Scope("manga", MediaType.Book, MediaSubtype.Manga, DiscoverFeedType.Trending, DiscoverFeedType.Top, DiscoverFeedType.New),
            Scope("music", MediaType.Music, null),
            Scope("games", MediaType.Game, null)
        ];

        public static bool IsCatalog(MediaType type, MediaSubtype? subtype) => subtype == MediaSubtype.Anime || type is not (MediaType.Movie or MediaType.Series);

        public static string Namespace(DiscoverScopeV1 scope) => scope.MediaSubtypeId is MediaSubtype.Anime or MediaSubtype.Manga or MediaSubtype.LightNovel
            ? "anilist"
            : scope.MediaTypeId switch
            {
                MediaType.Movie => "tmdb_movie",
                MediaType.Series => "tmdb_tv",
                MediaType.Book => "openlibrary",
                MediaType.Music => "musicbrainz",
                _ => "igdb"
            };

        private static DiscoverScopeV1 Scope(string slug, MediaType type, MediaSubtype? subtype, params DiscoverFeedType[] feeds) =>
            new(slug, type, subtype, IsCatalog(type, subtype), feeds);
    }
}
