using Jularr.Web.Features.Acquisition.Quality;

namespace Jularr.Web.Features.Acquisition.Selection;

public enum UpgradeState
{
    /// <summary>Nothing further is searched for: the cutoff is met, the best quality is installed, the profile does not upgrade or the installed quality cannot be compared.</summary>
    Final,

    /// <summary>A better quality may replace what is installed.</summary>
    Upgradable
}

public sealed record UpgradeAssessment(UpgradeState State, string Reason)
{
    public bool IsUpgradable => State == UpgradeState.Upgradable;
}

/// <summary>
/// The one cutoff and upgrade policy of every media type that installs a file per target (Movie, TV, Music, Books). It compares quality keys
/// of the profile's quality order, the same facts the selection engine ranks by, so a profile change takes effect on the next assessment
/// without any stored state of its own: the installed quality lives on the canonical Version.
/// A quality allowed only by a fallback tier is a temporary acceptance and stays upgradable even when the profile otherwise does not upgrade.
/// An installed quality that is not part of the profile's order is unknown and never replaced automatically, so a library that was scanned
/// instead of acquired is not churned.
/// </summary>
public static class UpgradePolicy
{
    /// <summary>How often a target that is installed but not final is searched again for a better release; a bounded poll, never a tight loop.</summary>
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

    /// <summary>The best (lowest-ranked) known quality among the installed versions of one target, or null when none is comparable.</summary>
    public static string? Best(QualityProfile profile, IEnumerable<string?> qualities) =>
        qualities
            .Where(quality => RankOf(profile, quality) != int.MaxValue)
            .OrderBy(quality => RankOf(profile, quality))
            .FirstOrDefault();

    public static UpgradeAssessment Assess(QualityProfile profile, string? installedQuality)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var rank = RankOf(profile, installedQuality);
        if (rank == int.MaxValue)
        {
            return new UpgradeAssessment(UpgradeState.Final, "The installed quality is unknown, so it is never replaced automatically.");
        }

        if (profile.AllowedQualities.Length > 0 && !profile.AllowedQualities.Contains(installedQuality!, StringComparer.OrdinalIgnoreCase))
        {
            return new UpgradeAssessment(UpgradeState.Upgradable, $"{installedQuality} was accepted temporarily; the profile's own qualities are still wanted.");
        }

        if (!profile.UpgradeAllowed)
        {
            return new UpgradeAssessment(UpgradeState.Final, "The profile does not upgrade.");
        }

        var cutoffRank = RankOf(profile, profile.UpgradeCutoffQuality);
        if (cutoffRank != int.MaxValue && rank <= cutoffRank)
        {
            return new UpgradeAssessment(UpgradeState.Final, $"{installedQuality} meets the cutoff {profile.UpgradeCutoffQuality}.");
        }

        return rank == 0
            ? new UpgradeAssessment(UpgradeState.Final, $"{installedQuality} is the best quality of the profile.")
            : new UpgradeAssessment(UpgradeState.Upgradable, $"{installedQuality} is below the cutoff {profile.UpgradeCutoffQuality ?? "of the profile"}.");
    }

    /// <summary>
    /// Whether a candidate of <paramref name="candidateQuality"/> is a meaningful upgrade of what is installed: the target is not final and the
    /// candidate is better by at least the profile's minimum number of quality steps, so a marginal difference never replaces a file.
    /// </summary>
    public static bool IsUpgrade(QualityProfile profile, string? installedQuality, string? candidateQuality)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!Assess(profile, installedQuality).IsUpgradable)
        {
            return false;
        }

        var installed = RankOf(profile, installedQuality);
        var candidate = RankOf(profile, candidateQuality);
        return candidate != int.MaxValue && installed - candidate >= Math.Max(1, profile.UpgradeMinimumQualitySteps);
    }
}
