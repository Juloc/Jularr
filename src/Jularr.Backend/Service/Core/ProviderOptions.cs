namespace Jularr.Backend.Service.Core
{
    // Metadata provider addresses; a mirror may replace them.
    public sealed class ProviderOptions
    {
        public Uri Tmdb { get; set; } = new("https://api.themoviedb.org/3/");
        public Uri Tvdb { get; set; } = new("https://api4.thetvdb.com/v4/");
        public Uri MusicBrainz { get; set; } = new("https://musicbrainz.org/ws/2/");
        public Uri AniList { get; set; } = new("https://graphql.anilist.co/");
        public Uri OpenLibrary { get; set; } = new("https://openlibrary.org/");
        public Uri Hardcover { get; set; } = new("https://api.hardcover.app/v1/");
        public Uri AcoustId { get; set; } = new("https://api.acoustid.org/v2/");

        // RapidOCR's model files; every one is checked against its pinned hash.
        public Uri OcrModels { get; set; } = new("https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/master/");

        // An image host (such as image.tmdb.org) a mirror serves in its place, for the covers Jularr keeps.
        public Dictionary<string, Uri> Images { get; set; } = [];

        // Keys travel along, so plain HTTP only stays on this machine.
        internal ProviderOptions Validated()
        {
            Uri[] addresses = [Tmdb, Tvdb, MusicBrainz, AniList, OpenLibrary, Hardcover, AcoustId, OcrModels, .. Images.Values];
            return addresses.All(address => address.IsAbsoluteUri && (address.Scheme == "https" || address.Scheme == "http" && address.IsLoopback)
                && address.AbsolutePath.EndsWith('/') && address.UserInfo.Length == 0 && address.Query.Length == 0)
                ? this
                : throw new InvalidOperationException("Provider addresses must be HTTPS addresses ending in a slash; plain HTTP only on loopback.");
        }
    }
}
