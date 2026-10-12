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
}
