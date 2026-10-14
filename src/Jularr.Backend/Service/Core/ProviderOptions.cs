namespace Jularr.Backend.Service.Core
{
    // Metadata provider addresses; a mirror may replace them.
    public sealed class ProviderOptions
    {
        public Uri Tmdb { get; set; } = new("https://api.themoviedb.org/3/");
        public Uri MusicBrainz { get; set; } = new("https://musicbrainz.org/ws/2/");
        public Uri AniList { get; set; } = new("https://graphql.anilist.co/");
        public Uri OpenLibrary { get; set; } = new("https://openlibrary.org/");

        internal IEnumerable<Uri> Addresses => [Tmdb, MusicBrainz, AniList, OpenLibrary];
    }
}
