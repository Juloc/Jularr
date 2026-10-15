using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Frontend.Framework
{
    // One Discover scope; films and series browse TMDB feeds, the other types search their own catalogs.
    public sealed record DiscoverType(string Slug, MediaType Type, MediaSubtype? Subtype, DiscoverFeedType[] Feeds)
    {
        public bool IsCatalog => Feeds.Length == 0;
    }

    // The Discover scopes in the order of the media-type bar.
    public static class DiscoverTypes
    {
        public static readonly DiscoverType[] All =
        [
            new("movies", MediaType.Movie, null, [DiscoverFeedType.Trending, DiscoverFeedType.Top, DiscoverFeedType.New, DiscoverFeedType.Upcoming]),
            new("series", MediaType.Series, null, [DiscoverFeedType.Trending, DiscoverFeedType.Top, DiscoverFeedType.New]),
            new("lightnovels", MediaType.Book, MediaSubtype.LightNovel, []),
            new("books", MediaType.Book, null, []),
            new("manga", MediaType.Book, MediaSubtype.Manga, []),
            new("music", MediaType.Music, null, []),
            new("games", MediaType.Game, null, [])
        ];

        public static DiscoverType? Find(string? slug) => All.FirstOrDefault(type => type.Slug == slug);

        // The TMDB scope of a provider title.
        public static DiscoverType For(MediaType type) => All.First(scope => scope.Type == type && !scope.IsCatalog);
    }
}
