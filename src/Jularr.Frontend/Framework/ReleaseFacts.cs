using System.Globalization;

namespace Jularr.Frontend.Framework
{
    // Age and size of a release as search results list them.
    public static class ReleaseFacts
    {
        public static string AgeDays(DateTime? publishedAt) =>
            publishedAt is { } date ? Math.Max(0, (int)(DateTime.UtcNow - date).TotalDays).ToString(CultureInfo.InvariantCulture) : "";

        public static string Size(long? bytes) => bytes is { } size ? ByteSize.Format(size) : "";
    }
}
