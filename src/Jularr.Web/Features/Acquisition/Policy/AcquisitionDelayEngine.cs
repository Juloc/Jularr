using Jularr.Web.Features.Acquisition.Monitoring;
using Jularr.Web.Features.Acquisition.Quality;

namespace Jularr.Web.Features.Acquisition.Policy;

public sealed record AcquisitionDelayDecision(bool Grab, DateTimeOffset? DelayedUntilUtc, string? Reason)
{
    public static AcquisitionDelayDecision NoDelay { get; } = new(true, null, null);
}

/// <summary>
/// Pure decision logic for delay profiles (P1 item 4) and tag-scoped indexer restrictions (item 5).
/// The pipeline calls these once it has resolved the anime's tags and quality profile; nothing here
/// touches Prowlarr, SABnzbd or persisted state.
/// </summary>
public static class AcquisitionDelayEngine
{
    /// <summary>
    /// Picks the most specific matching delay profile: a profile scoped to both the quality profile
    /// and a shared tag outranks one scoped to only one of them, which outranks an unscoped
    /// (global) profile. A profile whose configured quality profile or tags do not match the anime
    /// never applies, even if marked default.
    /// </summary>
    public static AnimeDelayProfile? SelectProfile(
        IReadOnlyList<AnimeDelayProfile> profiles,
        string? qualityProfileId,
        IReadOnlyCollection<string>? tagIds)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var tags = new HashSet<string>(tagIds ?? [], StringComparer.OrdinalIgnoreCase);

        AnimeDelayProfile? best = null;
        var bestScore = -1;
        foreach (var profile in profiles)
        {
            if (profile.QualityProfileId is { Length: > 0 } scopedProfile &&
                !scopedProfile.Equals(qualityProfileId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (profile.TagIds.Length > 0 && !profile.TagIds.Any(tags.Contains))
            {
                continue;
            }

            var score = (profile.QualityProfileId is { Length: > 0 } ? 2 : 0) + (profile.TagIds.Length > 0 ? 1 : 0);
            if (score > bestScore || (score == bestScore && profile.IsDefault && best?.IsDefault != true))
            {
                best = profile;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>
    /// A delay profile as the selection engine's own wait: the qualities that reach the profile's cutoff stay allowed at once and the rest are
    /// added as a fallback tier after the delay, so "wait for a preferred release, then take what exists" is the same timed ladder every
    /// media type uses. Without a cutoff the best quality of the profile is the preferred one. Returns the profile itself when no delay applies.
    /// </summary>
    public static AnimeQualityProfile WithDelayAsFallbackTier(AnimeQualityProfile profile, AnimeDelayProfile? delayProfile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (delayProfile is null || delayProfile.DelayMinutes <= 0)
        {
            return profile;
        }

        string[] universe = profile.AllowedQualities.Length > 0 ? profile.AllowedQualities : profile.QualityOrder;
        var cutoffRank = Selection.UpgradePolicy.RankOf(profile, profile.UpgradeCutoffQuality);
        var preferred = universe.Where(quality => Selection.UpgradePolicy.RankOf(profile, quality) <= (cutoffRank == int.MaxValue ? 0 : cutoffRank)).ToArray();
        var rest = universe.Except(preferred, StringComparer.OrdinalIgnoreCase).ToArray();
        if (preferred.Length == 0 || rest.Length == 0)
        {
            return profile;
        }

        return profile with
        {
            AllowedQualities = preferred,
            FallbackTiers = [.. profile.FallbackTiers.Append(new FallbackTier(delayProfile.DelayMinutes, rest)).OrderBy(tier => tier.AfterMinutes)]
        };
    }

    /// <summary>
    /// Whether the candidate should be grabbed now, or held back until <c>DelayedUntilUtc</c>
    /// (unless a preferred release — one that already meets the profile's upgrade cutoff quality —
    /// appears sooner, in which case the delay is skipped).
    /// </summary>
    public static AcquisitionDelayDecision Evaluate(
        AnimeDelayProfile? delayProfile,
        AnimeQualityProfile qualityProfile,
        AnimeReleaseScoreResult candidate,
        DateTimeOffset becameWantedAtUtc,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(qualityProfile);
        ArgumentNullException.ThrowIfNull(candidate);

        if (delayProfile is null || delayProfile.DelayMinutes <= 0)
        {
            return AcquisitionDelayDecision.NoDelay;
        }

        if (AnimeMonitoringEngine.IsCutoffMet(qualityProfile, candidate))
        {
            return new(true, null, $"Preferred release quality already met; delay profile '{delayProfile.Name}' skipped.");
        }

        var delayedUntil = becameWantedAtUtc + TimeSpan.FromMinutes(delayProfile.DelayMinutes);
        if (now >= delayedUntil)
        {
            return AcquisitionDelayDecision.NoDelay;
        }

        return new(
            false,
            delayedUntil,
            $"Delayed by profile '{delayProfile.Name}' until {delayedUntil:u}, waiting for a preferred release.");
    }

    /// <summary>
    /// Canonical indexer entry ids (a whole Prowlarr entry or a direct Newznab entry alike)
    /// the anime's tags restrict searches to, or null when no restriction applies. Two or more
    /// applicable restrictions intersect (an anime tagged for both is limited to entries every
    /// applicable restriction allows).
    /// </summary>
    public static Guid[]? RestrictedIndexerEntryIds(
        IReadOnlyList<AnimeIndexerRestriction> restrictions,
        IReadOnlyCollection<string>? tagIds)
    {
        var applicable = ApplicableIndexerRestrictions(restrictions, tagIds);
        if (applicable.Length == 0)
        {
            return null;
        }

        IEnumerable<Guid> allowed = applicable[0].AllowedIndexerEntryIds;
        foreach (var restriction in applicable.Skip(1))
        {
            allowed = allowed.Intersect(restriction.AllowedIndexerEntryIds);
        }

        return allowed.Distinct().Order().ToArray();
    }

    /// <summary>The names of the tag-scoped indexer restrictions that apply to this anime's tags,
    /// for a clear "why" message when the intersection with the anime's own selection is empty.</summary>
    public static string[] ApplicableIndexerRestrictionNames(
        IReadOnlyList<AnimeIndexerRestriction> restrictions,
        IReadOnlyCollection<string>? tagIds) =>
        ApplicableIndexerRestrictions(restrictions, tagIds).Select(restriction => restriction.Name).ToArray();

    private static AnimeIndexerRestriction[] ApplicableIndexerRestrictions(
        IReadOnlyList<AnimeIndexerRestriction> restrictions,
        IReadOnlyCollection<string>? tagIds)
    {
        ArgumentNullException.ThrowIfNull(restrictions);
        var tags = new HashSet<string>(tagIds ?? [], StringComparer.OrdinalIgnoreCase);
        return restrictions
            .Where(restriction => restriction.TagIds.Length > 0 && restriction.TagIds.Any(tags.Contains))
            .ToArray();
    }
}
