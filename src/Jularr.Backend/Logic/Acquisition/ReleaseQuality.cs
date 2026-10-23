using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;

namespace Jularr.Backend.Logic.Acquisition
{
    // A release's quality under the name Sonarr (series) or Radarr (films) gives it; books, music and games name their format.
    internal static class ReleaseQuality
    {
        public static string GetKey(ReleaseInfo release, bool movie = false)
        {
            if (!string.IsNullOrWhiteSpace(release.DocumentFormat))
            {
                return release.DocumentFormat.Trim().ToUpperInvariant();
            }

            // Only a resolution reads as television, as in Sonarr; SD is 480p.
            var resolution = release.Resolution switch
            {
                >= 2160 => 2160,
                >= 1080 => 1080,
                >= 720 => 720,
                576 => 576,
                > 0 => 480,
                _ => (int?)null
            };
            var remux = release.Modifier == ReleaseQualityModifier.Remux;
            return release.Source switch
            {
                ReleaseSource.Workprint when movie => "WORKPRINT",
                ReleaseSource.Cam when movie => "CAM",
                ReleaseSource.Telesync when movie => "TELESYNC",
                ReleaseSource.Telecine when movie => "TELECINE",
                ReleaseSource.Dvd when movie && release.Modifier == ReleaseQualityModifier.Screener => "DVDSCR",
                _ when movie && release.Modifier == ReleaseQualityModifier.Regional => "REGIONAL",
                ReleaseSource.Dvd when movie && remux => "DVD-R",
                ReleaseSource.Dvd => "DVD",
                ReleaseSource.Sdtv => "SDTV",
                ReleaseSource.WebDl => "WEBDL-" + Web(resolution) + "p",
                ReleaseSource.WebRip => "WEBRip-" + Web(resolution) + "p",
                ReleaseSource.BluRay or ReleaseSource.BluRayRip when movie && release.Modifier == ReleaseQualityModifier.BrDisk => "BR-DISK",
                ReleaseSource.BluRay or ReleaseSource.BluRayRip when remux && resolution is 1080 or 2160 => movie ? "Remux-" + resolution + "p" : "Bluray-" + resolution + "p Remux",
                ReleaseSource.BluRay or ReleaseSource.BluRayRip => "Bluray-" + (resolution ?? 720) + "p",
                ReleaseSource.Hdtv or ReleaseSource.Unknown when release.Modifier == ReleaseQualityModifier.RawHd => "Raw-HD",
                ReleaseSource.Hdtv or ReleaseSource.Unknown when resolution is 720 or 1080 or 2160 => "HDTV-" + resolution + "p",
                ReleaseSource.Hdtv => "SDTV",
                _ when resolution is not null => "SDTV",
                _ => "Unknown"
            };
        }

        // Web releases come in 480p, 720p, 1080p and 2160p.
        private static int Web(int? resolution) => resolution is null or 576 ? 480 : resolution.Value;
    }
}
