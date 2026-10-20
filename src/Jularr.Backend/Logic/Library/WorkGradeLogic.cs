using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Library;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;

namespace Jularr.Backend.Logic.Library
{
    // How good a stored file is for its title: its quality against the profile's cutoff and its size against the profile's range for that quality.
    internal static class WorkGradeLogic
    {
        // Codecs that reach the same picture in far fewer bytes; a file in one of them may be much smaller before it counts as small.
        private static readonly string[] EfficientCodecs = ["hevc", "h265", "av1", "vp9"];
        private const decimal EfficientMinimumFactor = 0.5m;

        public static FileQualityGrade Quality(QualityProfile? profile, string? qualityKey)
        {
            if (profile is null || string.IsNullOrWhiteSpace(qualityKey))
            {
                return FileQualityGrade.Unknown;
            }

            if (profile.AllowedQualities.Length > 0 && !profile.AllowedQualities.Contains(profile.Item(qualityKey), StringComparer.OrdinalIgnoreCase))
            {
                return FileQualityGrade.NotAllowed;
            }

            return UpgradePolicy.Assess(profile, qualityKey).State == UpgradeState.Upgradable ? FileQualityGrade.Upgrade : FileQualityGrade.Met;
        }

        // Megabytes per minute of the file's own length, else of the title's runtime, against the quality's limits.
        public static (FileSizeGrade Grade, decimal? MegabytesPerMinute) Size(QualityProfile? profile, string? qualityKey, long sizeBytes, long? durationMs, string? videoCodec)
        {
            decimal? minutes = durationMs is > 0 ? durationMs.Value / 60000m : profile?.RuntimeMinutes;
            if (minutes is not > 0)
            {
                return (FileSizeGrade.Unknown, null);
            }

            var perMinute = Math.Round(sizeBytes / 1_000_000m / minutes.Value, 1);
            if (profile is null || qualityKey is null || !profile.Sizes.TryGetValue(qualityKey, out var limits))
            {
                return (FileSizeGrade.Unknown, perMinute);
            }

            if (limits.Maximum is > 0 && perMinute > limits.Maximum)
            {
                return (FileSizeGrade.Large, perMinute);
            }

            var efficient = videoCodec is not null && EfficientCodecs.Any(codec => videoCodec.Contains(codec, StringComparison.OrdinalIgnoreCase));
            return (perMinute < limits.Minimum * (efficient ? EfficientMinimumFactor : 1m) ? FileSizeGrade.Small : FileSizeGrade.Good, perMinute);
        }
    }
}
