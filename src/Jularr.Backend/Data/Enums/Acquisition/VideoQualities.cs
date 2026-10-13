using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Data.Enums.Acquisition
{
    // Video quality keys from source and resolution, best first.
    public static class VideoQualities
    {
        public static IReadOnlyList<string> Order { get; } =
        [
            "BLURAY-2160p", "WEB-2160p", "HDTV-2160p",
            "BLURAY-1080p", "WEB-1080p", "HDTV-1080p",
            "BLURAY-720p", "WEB-720p", "HDTV-720p",
            "BLURAY-576p", "WEB-576p", "HDTV-576p",
            "BLURAY-480p", "WEB-480p", "HDTV-480p"
        ];
    }

    // Ebook formats, best first; EBOOK is a release that does not name its format.
    public static class BookQualities
    {
        public static IReadOnlyList<string> Order { get; } = ["EPUB", "AZW3", "MOBI", "PDF", "EBOOK"];
    }

    // Manga formats, best first; COMIC is a release that does not name its format.
    public static class MangaQualities
    {
        public static IReadOnlyList<string> Order { get; } = ["CBZ", "CBR", "EPUB", "PDF", "COMIC"];
    }

    public static class ProfileQualities
    {
        // Media types with release qualities; empty means no profiles.
        public static IReadOnlyList<string> For(MediaType mediaType) => mediaType switch
        {
            MediaType.Movie or MediaType.Series => VideoQualities.Order,
            MediaType.Book or MediaType.LightNovel => BookQualities.Order,
            MediaType.Manga => MangaQualities.Order,
            _ => []
        };
    }
}
