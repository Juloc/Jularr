using System.Globalization;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;

namespace Jularr.Backend.Logic.Acquisition
{
    internal static class ReleaseQuality
    {
        public static string GetKey(ReleaseInfo release)
        {
            if (!string.IsNullOrWhiteSpace(release.DocumentFormat))
            {
                return release.DocumentFormat.Trim().ToUpperInvariant();
            }

            var source = release.Source switch
            {
                ReleaseSource.WebDl or ReleaseSource.WebRip => "WEB",
                ReleaseSource.BluRay or ReleaseSource.BluRayRip => "BLURAY",
                ReleaseSource.Hdtv => "HDTV",
                _ => "UNKNOWN"
            };

            var resolution = release.Resolution is > 0
                ? $"{release.Resolution}p"
                : "UNKNOWN";

            return $"{source}-{resolution}";
        }
    }
}
