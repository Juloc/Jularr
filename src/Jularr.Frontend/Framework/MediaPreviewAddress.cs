using System.Globalization;

namespace Jularr.Frontend.Framework
{
    // Where a card's delayed hover preview comes from: the Work the card shows.
    public static class MediaPreviewAddress
    {
        public const string Path = "/MediaPreview";

        public static string Work(long workId) => Path + "?work=" + workId.ToString(CultureInfo.InvariantCulture);
    }
}
