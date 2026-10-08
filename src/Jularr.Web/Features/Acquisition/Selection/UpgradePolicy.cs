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
/// The one cutoff and upgrade policy of every media type (Movie, TV, Anime, Music, Books). It compares quality keys of the profile's quality
/// order, the same facts the selection engine ranks by, so a profile change takes effect on the next assessment without any stored state of its
/// own: the installed quality lives on the canonical Version, or is read from the installed file's name where a media type keeps its files
/// named by release.
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

    /// <summary>Whether the installed quality reaches the profile's upgrade cutoff; false when the profile has no cutoff or the quality cannot be compared.</summary>
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
            return new UpgradeAssessment(UpgradeState.Final, "The installed quality is unknown, so it is never replaced automatically.");
        }

        if (!profile.UpgradeAllowed)
        {
            return new UpgradeAssessment(UpgradeState.Final, "The profile does not upgrade.");
        }

        if (IsCutoffMet(profile, installedQuality))
        {
            return new UpgradeAssessment(UpgradeState.Final, $"{installedQuality} meets the cutoff {profile.UpgradeCutoffQuality}.");
        }

        return rank == 0
            ? new UpgradeAssessment(UpgradeState.Final, $"{installedQuality} is the best quality of the profile.")
            : new UpgradeAssessment(UpgradeState.Upgradable, $"{installedQuality} is below the cutoff {profile.UpgradeCutoffQuality ?? "of the profile"}.");
    }

    /// <summary>
    /// Whether a candidate is a meaningful upgrade of what is installed: the target is not final and the candidate is better by at least the
    /// profile's minimum number of quality steps.
    /// An installed quality outside the profile's order is only replaced when <paramref name="upgradeUnknownInstalled"/> says a better candidate
    /// always wins (a media type whose files are named by release and managed entirely by Jularr); otherwise it is left alone.
    /// </summary>
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
