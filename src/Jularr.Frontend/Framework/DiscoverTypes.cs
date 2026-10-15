using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Frontend.Framework
{
    // One Discover scope; films and series come from TMDB, anime and the other types from their own catalogs.
    public sealed record DiscoverType(string Slug, MediaType Type, MediaSubtype? Subtype, DiscoverFeedType[] Feeds)
    {
        public bool IsCatalog => Subtype == MediaSubtype.Anime || Type is not (MediaType.Movie or MediaType.Series);
    }

    // The Discover scopes in the order of the media-type bar.
    public static class DiscoverTypes
    {
        public static readonly DiscoverType[] All =
        [
            new("movies", MediaType.Movie, null, [DiscoverFeedType.Trending, DiscoverFeedType.Top, DiscoverFeedType.New, DiscoverFeedType.Upcoming]),
            new("series", MediaType.Series, null, [DiscoverFeedType.Trending, DiscoverFeedType.Top, DiscoverFeedType.New]),
            new("anime", MediaType.Series, MediaSubtype.Anime, [DiscoverFeedType.Trending, DiscoverFeedType.Top, DiscoverFeedType.New]),
            new("lightnovels", MediaType.Book, MediaSubtype.LightNovel, [DiscoverFeedType.Trending, DiscoverFeedType.Top, DiscoverFeedType.New]),
            new("books", MediaType.Book, null, [DiscoverFeedType.Trending]),
            new("manga", MediaType.Book, MediaSubtype.Manga, [DiscoverFeedType.Trending, DiscoverFeedType.Top, DiscoverFeedType.New]),
            new("music", MediaType.Music, null, []),
            new("games", MediaType.Game, null, [])
        ];

        public static DiscoverType? Find(string? slug) => All.FirstOrDefault(type => type.Slug == slug);

        // The TMDB scope of a provider title.
        public static DiscoverType For(MediaType type) => All.First(scope => scope.Type == type && !scope.IsCatalog);
    }
}
