using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.User.Profile.V1;

namespace Jularr.Frontend.Framework
{
    // The media types a personal list can be narrowed to; types the profile hides are left out.
    public static class LibraryScopes
    {
        public static IReadOnlyList<LibraryScope> All { get; } =
        [
            new("all", null, null), new("movies", MediaType.Movie, null), new("series", MediaType.Series, null), new("anime", null, MediaSubtype.Anime), new("books", MediaType.Book, null),
            new("manga", null, MediaSubtype.Manga), new("lightnovels", null, MediaSubtype.LightNovel), new("music", MediaType.Music, null), new("games", MediaType.Game, null)
        ];

        // The scope a Work belongs to: its subtype, else its type.
        public static string Slug(MediaType type, MediaSubtype? subtype) =>
            All.FirstOrDefault(scope => subtype is { } value ? scope.Subtype == value : scope.Type == type)?.Slug ?? "all";

        public static LibraryScope Find(string? slug) => All.FirstOrDefault(scope => scope.Slug == slug) ?? All[0];

        public static IEnumerable<LibraryScope> Listed(IReadOnlyList<ProfileMediaV1> media) =>
            All.Where(scope => scope.Slug == "all" || media.Any(item => !item.IsHidden && item.Key == (scope.Subtype?.ToString() ?? scope.Type?.ToString())));
    }

    public sealed record LibraryScope(string Slug, MediaType? Type, MediaSubtype? Subtype);
}
