using System.Globalization;
using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Frontend.Framework
{
    // Where a card's delayed hover preview comes from: a Work, a title of a catalog or a TMDB title that is no Work yet.
    public static class MediaPreviewAddress
    {
        public const string Path = "/MediaPreview";

        public static string Work(long workId) => Path + "?work=" + workId.ToString(CultureInfo.InvariantCulture);

        public static string Catalog(string scope, string @namespace, string externalId) =>
            Path + "?scope=" + Uri.EscapeDataString(scope) + "&namespace=" + Uri.EscapeDataString(@namespace) + "&id=" + Uri.EscapeDataString(externalId);

        public static string Tmdb(MediaType type, int tmdbId) => Path + "?type=" + type + "&tmdb=" + tmdbId.ToString(CultureInfo.InvariantCulture);
    }
}
