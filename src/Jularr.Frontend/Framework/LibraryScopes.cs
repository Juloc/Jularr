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

    // The media types of an address (`type=a,b` or repeated) against the types the profile lists: which are chosen, whether that is all of them,
    // and the type and subtype filters a list read takes. Without the profile's media (not read yet) every slug of the address counts as chosen.
    public sealed class ScopeSelection(IEnumerable<string> requested, IReadOnlyList<ProfileMediaV1>? media)
    {
        // The slugs of the address, whatever the profile lists.
        public IReadOnlyList<string> Picked { get; } = [.. requested.SelectMany(type => type.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(slug => slug != "all" && LibraryScopes.All.Any(scope => scope.Slug == slug)).Distinct()];

        public IReadOnlyList<LibraryScope> Listed { get; } = media is null ? [] : [.. LibraryScopes.Listed(media).Where(scope => scope.Slug != "all")];

        // The listed types asked for, or every listed type.
        public IReadOnlyList<LibraryScope> Chosen => Listed.Where(scope => Picked.Contains(scope.Slug)).ToList() is { Count: > 0 } picked ? picked : Listed;

        public bool IsAll => Chosen.Count == Listed.Count;

        public IReadOnlyList<string> ListedSlugs => [.. Listed.Select(scope => scope.Slug)];

        public IReadOnlyList<string> ChosenSlugs => [.. Chosen.Select(scope => scope.Slug)];

        // A type lists its titles without subtype and a subtype lists that subtype across types; all types need no filter.
        public (MediaType[] Types, MediaSubtype[] Subtypes) Filter() => IsAll ? ([], [])
            : ([.. Chosen.Where(scope => scope.Subtype is null && scope.Type is not null).Select(scope => scope.Type!.Value)], [.. Chosen.Where(scope => scope.Subtype is not null).Select(scope => scope.Subtype!.Value)]);

        // The address value of some types: none for all of them (once the profile's types are known).
        public string? Parameter(IReadOnlyList<string> types) =>
            types.Count == 0 || media is not null && Listed.All(scope => types.Contains(scope.Slug)) ? null : string.Join(',', types);
    }

    // The release years a personal list can be narrowed to: since 2020 or before.
    public static class YearChoice
    {
        public static string? Valid(string? year) => year is "2020" or "old" ? year : null;

        public static (int? From, int? To) Range(string? year) => Valid(year) switch { "2020" => (2020, null), "old" => (null, 2019), _ => (null, null) };
    }
}
