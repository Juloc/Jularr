using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;

namespace Jularr.Backend.Logic.Acquisition
{
    // The one cutoff and upgrade policy of every media type (Movie, TV, Anime, Music, Books).
    internal static class UpgradePolicy
    {
        // How often a target that is installed but not final is searched again for a better release.
        public static readonly TimeSpan SearchInterval = TimeSpan.FromHours(12);

        public static int RankOf(QualityProfile profile, string? quality)
        {
            if (string.IsNullOrWhiteSpace(quality))
            {
                return int.MaxValue;
            }

            var index = Array.FindIndex(profile.QualityOrder, item => item.Equals(quality, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? int.MaxValue : index;
        }

        // The best (lowest-ranked) known quality among the installed versions of one target, or null when none is comparable.
        public static string? Best(QualityProfile profile, IEnumerable<string?> qualities) =>
            qualities
                .Where(quality => RankOf(profile, quality) != int.MaxValue)
                .OrderBy(quality => RankOf(profile, quality))
                .FirstOrDefault();

        // Whether the installed quality reaches the profile's upgrade cutoff.
        public static bool IsCutoffMet(QualityProfile profile, string? installedQuality)
        {
            var cutoffRank = RankOf(profile, profile.UpgradeCutoffQuality);
            var rank = RankOf(profile, installedQuality);
            return cutoffRank != int.MaxValue && rank != int.MaxValue && rank <= cutoffRank;
        }

        public static UpgradeAssessment Assess(QualityProfile profile, string? installedQuality)
        {
            ArgumentNullException.ThrowIfNull(profile);

            var rank = RankOf(profile, installedQuality);
            if (rank == int.MaxValue)
            {
                return new UpgradeAssessment(UpgradeState.Final, new ReleaseReason("InstalledQualityUnknown"));
            }

            if (!profile.UpgradeAllowed)
            {
                return new UpgradeAssessment(UpgradeState.Final, new ReleaseReason("UpgradesDisabled"));
            }

            if (IsCutoffMet(profile, installedQuality))
            {
                return new UpgradeAssessment(UpgradeState.Final, new ReleaseReason("CutoffMet", installedQuality));
            }

            return rank == 0
                ? new UpgradeAssessment(UpgradeState.Final, new ReleaseReason("BestQuality", installedQuality))
                : new UpgradeAssessment(UpgradeState.Upgradable, new ReleaseReason("BelowCutoff", installedQuality));
        }

        // Whether a candidate is a meaningful upgrade of what is installed.
        public static bool IsUpgrade(QualityProfile profile, string? installedQuality, string? candidateQuality, bool upgradeUnknownInstalled = false)
        {
            ArgumentNullException.ThrowIfNull(profile);

            var installed = RankOf(profile, installedQuality);
            var candidate = RankOf(profile, candidateQuality);
            if (installed == int.MaxValue && !upgradeUnknownInstalled)
            {
                return false;
            }

            var steps = Math.Max(1, profile.UpgradeMinimumQualitySteps);
            if (!profile.UpgradeAllowed || IsCutoffMet(profile, installedQuality))
            {
                return false;
            }

            return candidate < installed && (installed == int.MaxValue || installed - candidate >= steps);
        }
    }
}
