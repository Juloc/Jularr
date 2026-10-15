namespace Jularr.Backend.Service.Core
{
    // Metadata provider addresses; a mirror may replace them.
    public sealed class ProviderOptions
    {
        public Uri Tmdb { get; set; } = new("https://api.themoviedb.org/3/");
        public Uri MusicBrainz { get; set; } = new("https://musicbrainz.org/ws/2/");
        public Uri AniList { get; set; } = new("https://graphql.anilist.co/");
        public Uri OpenLibrary { get; set; } = new("https://openlibrary.org/");
        public Uri Hardcover { get; set; } = new("https://api.hardcover.app/v1/");
        public Uri AcoustId { get; set; } = new("https://api.acoustid.org/v2/");

        // Keys travel along, so plain HTTP only stays on this machine.
        internal ProviderOptions Validated()
        {
            Uri[] addresses = [Tmdb, MusicBrainz, AniList, OpenLibrary, Hardcover, AcoustId];
            return addresses.All(address => address.IsAbsoluteUri && (address.Scheme == "https" || address.Scheme == "http" && address.IsLoopback)
                && address.AbsolutePath.EndsWith('/') && address.UserInfo.Length == 0 && address.Query.Length == 0)
                ? this
                : throw new InvalidOperationException("Provider addresses must be HTTPS addresses ending in a slash; plain HTTP only on loopback.");
        }
    }
}
