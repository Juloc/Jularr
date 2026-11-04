using Jularr.Backend.Data.Enums.Media;
using System.Globalization;

namespace Jularr.Backend.Data.Enums.Acquisition
{
    // Sonarr's qualities for series, best first; a remux carries Radarr's name, so one profile serves films and series.
    public static class SeriesQualities
    {
        public static IReadOnlyList<string> Order { get; } =
        [
            "Remux-2160p", "Bluray-2160p", "WEBDL-2160p", "WEBRip-2160p", "HDTV-2160p",
            "Remux-1080p", "Bluray-1080p", "WEBDL-1080p", "WEBRip-1080p", "HDTV-1080p", "Raw-HD",
            "Bluray-720p", "WEBDL-720p", "WEBRip-720p", "HDTV-720p",
            "Bluray-576p", "Bluray-480p", "WEBDL-480p", "WEBRip-480p", "DVD", "SDTV", "Unknown"
        ];
    }

    // Radarr's qualities for films, best first.
    public static class MovieQualities
    {
        public static IReadOnlyList<string> Order { get; } =
        [
            "Remux-2160p", "Bluray-2160p", "WEBDL-2160p", "WEBRip-2160p", "HDTV-2160p",
            "Remux-1080p", "Bluray-1080p", "WEBDL-1080p", "WEBRip-1080p", "HDTV-1080p", "Raw-HD",
            "Bluray-720p", "WEBDL-720p", "WEBRip-720p", "HDTV-720p", "BR-DISK",
            "Bluray-576p", "Bluray-480p", "WEBDL-480p", "WEBRip-480p", "DVD-R", "DVD", "SDTV",
            "DVDSCR", "REGIONAL", "TELECINE", "TELESYNC", "CAM", "WORKPRINT", "Unknown"
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

    // Music formats, best first; AUDIO is a release that does not name its format.
    public static class MusicQualities
    {
        public static IReadOnlyList<string> Order { get; } = ["FLAC-24", "FLAC", "ALAC", "MP3-320", "MP3-V0", "MP3", "AAC", "AUDIO"];
    }

    // Game release forms, best first; GAME is a release that does not name its form.
    public static class GameQualities
    {
        public static IReadOnlyList<string> Order { get; } = ["GOG", "NSP", "XCI", "ISO", "ROM", "GAME"];
    }

    public static class ProfileQualities
    {
        public static string Group(long id) => "group:" + id.ToString(CultureInfo.InvariantCulture);

        // Sonarr's name of a remux ("Bluray-1080p Remux") as Jularr names it ("Remux-1080p").
        public static string? Canonical(string? quality) => quality is { } name && name.StartsWith("Bluray-", StringComparison.OrdinalIgnoreCase) && name.EndsWith("p Remux", StringComparison.OrdinalIgnoreCase)
            ? "Remux-" + name["Bluray-".Length..^" Remux".Length]
            : quality;

        // The qualities a file of a media type can have.
        public static IReadOnlyList<string> For(MediaType mediaType, MediaSubtype? subtype) => mediaType switch
        {
            MediaType.Movie => MovieQualities.Order,
            MediaType.Series => SeriesQualities.Order,
            MediaType.Book => subtype == MediaSubtype.Manga ? MangaQualities.Order : BookQualities.Order,
            MediaType.Music => MusicQualities.Order,
            MediaType.Game => GameQualities.Order,
            _ => []
        };

        // The ladder of a profile: films, series and anime share Radarr's list (series never meet its film-only qualities).
        public static IReadOnlyList<string> For(QualityFamily family) => family switch
        {
            QualityFamily.Video => MovieQualities.Order,
            QualityFamily.Book => BookQualities.Order,
            QualityFamily.Manga => MangaQualities.Order,
            QualityFamily.Music => MusicQualities.Order,
            _ => GameQualities.Order
        };

        // A new profile starts like Sonarr's and Radarr's HD-1080p and upgrades up to a web download; other kinds with their best format and no upgrades.
        public static string[] StartingQualities(QualityFamily family) => family == QualityFamily.Video
            ? ["Bluray-1080p", "WEBDL-1080p", "WEBRip-1080p", "HDTV-1080p"]
            : For(family).Take(1).ToArray();

        public static string? StartingCutoff(QualityFamily family) => family == QualityFamily.Video ? "WEBDL-1080p" : null;
    }
}
