using System.Text.RegularExpressions;
using Jularr.Web.Features.Acquisition.Release;

namespace Jularr.Web.Features.Acquisition.Quality;

// Media-type-agnostic release scorer. Operates only on the parsed ReleaseInfo and a QualityProfile,
// so it is shared by every media type; anime is one caller.
public static class ReleaseScorer
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static ReleaseScoreResult Score(
        QualityProfile profile,
        ReleaseCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(candidate.Release);

        var validation = ValidateProfile(profile);
        if (validation.Count > 0)
        {
            throw new ArgumentException(
                $"Quality profile '{profile.Id}' is invalid: {string.Join("; ", validation)}",
                nameof(profile));
        }

        var qualityKey = ReleaseQuality.GetKey(candidate.Release);
        var qualityRank = IndexOf(profile.QualityOrder, qualityKey);
        var rejections = new List<string>();
        var scoreReasons = new List<string>();

        if (profile.AllowedQualities.Length > 0 &&
            !profile.AllowedQualities.Contains(qualityKey, StringComparer.OrdinalIgnoreCase))
        {
            rejections.Add($"Quality '{qualityKey}' is not allowed.");
        }

        if ((profile.MinimumSizeBytes is not null || profile.MaximumSizeBytes is not null) &&
            candidate.SizeBytes is null)
        {
            rejections.Add("Size is unknown but this profile has size limits.");
        }
        else
        {
            if (profile.MinimumSizeBytes is long minimumSize &&
                candidate.SizeBytes is long actualSize &&
                actualSize < minimumSize)
            {
                rejections.Add($"Size {actualSize} B is below minimum {minimumSize} B.");
            }

            if (profile.MaximumSizeBytes is long maximumSize &&
                candidate.SizeBytes is long actualMaximumSize &&
                actualMaximumSize > maximumSize)
            {
                rejections.Add($"Size {actualMaximumSize} B is above maximum {maximumSize} B.");
            }
        }

        foreach (var required in profile.MustContain)
        {
            if (!candidate.Release.RawTitle.Contains(required, StringComparison.OrdinalIgnoreCase))
            {
                rejections.Add($"Missing required term '{required}'.");
            }
        }

        foreach (var rejected in profile.MustNotContain)
        {
            if (candidate.Release.RawTitle.Contains(rejected, StringComparison.OrdinalIgnoreCase))
            {
                rejections.Add($"Contains rejected term '{rejected}'.");
            }
        }

        foreach (var pattern in profile.RequiredRegex)
        {
            if (!Regex.IsMatch(
                    candidate.Release.RawTitle,
                    pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    RegexTimeout))
            {
                rejections.Add($"Does not match required regex '{pattern}'.");
            }
        }

        foreach (var pattern in profile.RejectedRegex)
        {
            if (Regex.IsMatch(
                    candidate.Release.RawTitle,
                    pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    RegexTimeout))
            {
                rejections.Add($"Matches rejected regex '{pattern}'.");
            }
        }

        var score = 0;
        var infoReasons = new List<string>();
        foreach (var rule in profile.ScoreRules)
        {
            var matched = Matches(rule, candidate.Release);
            switch (rule.EffectiveEffect)
            {
                case ReleaseRuleEffect.Require when !matched:
                    rejections.Add($"Missing required rule '{rule.Name}'.");
                    break;
                case ReleaseRuleEffect.Reject when matched:
                    rejections.Add($"Matches reject rule '{rule.Name}'.");
                    break;
                case ReleaseRuleEffect.Info when matched:
                    infoReasons.Add(rule.Name);
                    break;
                case ReleaseRuleEffect.Prefer or ReleaseRuleEffect.Avoid when matched:
                    score += rule.Score;
                    scoreReasons.Add($"{rule.Name}: {(rule.Score >= 0 ? "+" : "")}{rule.Score}");
                    break;
            }
        }

        if (score < profile.MinimumScore)
        {
            rejections.Add($"Score {score} is below minimum {profile.MinimumScore}.");
        }

        return new ReleaseScoreResult(
            candidate,
            Accepted: rejections.Count == 0,
            score,
            qualityKey,
            qualityRank,
            rejections,
            scoreReasons)
        {
            InfoReasons = infoReasons
        };
    }

    public static IReadOnlyList<ReleaseScoreResult> Rank(
        QualityProfile profile,
        IEnumerable<ReleaseCandidate> candidates) =>
        candidates
            .Select(candidate => Score(profile, candidate))
            .OrderByDescending(result => result.Accepted)
            .ThenBy(result => result.QualityRank)
            .ThenByDescending(result => result.Score)
            .ThenBy(result => result.Candidate.Release.RawTitle, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>Whether a scored release is a meaningful upgrade of the scored current file: the shared <see cref="Selection.UpgradePolicy"/>, where a file whose quality cannot be read is replaced by any release of a known quality.</summary>
    public static bool IsUpgrade(
        QualityProfile profile,
        ReleaseScoreResult current,
        ReleaseScoreResult candidate)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(candidate);

        return candidate.Accepted
            && Selection.UpgradePolicy.IsUpgrade(profile, current.QualityKey, candidate.QualityKey, current.Score, candidate.Score, upgradeUnknownInstalled: true);
    }

    public static IReadOnlyList<string> ValidateProfile(QualityProfile profile)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            errors.Add("Profile ID is required.");
        }

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            errors.Add("Profile name is required.");
        }

        if (profile.AllowedQualities is null ||
            profile.QualityOrder is null ||
            profile.MustContain is null ||
            profile.MustNotContain is null ||
            profile.RequiredRegex is null ||
            profile.RejectedRegex is null ||
            profile.ScoreRules is null)
        {
            errors.Add("Profile collections must not be null.");
            return errors;
        }

        if (profile.MinimumSizeBytes is < 0)
        {
            errors.Add("Minimum size must be zero or greater.");
        }

        if (profile.MaximumSizeBytes is < 0)
        {
            errors.Add("Maximum size must be zero or greater.");
        }

        if (profile.MinimumSizeBytes is long minimum &&
            profile.MaximumSizeBytes is long maximum &&
            minimum > maximum)
        {
            errors.Add("Minimum size must not exceed maximum size.");
        }

        if (profile.QualityOrder
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
        {
            errors.Add("Quality order contains duplicates.");
        }

        foreach (var allowed in profile.AllowedQualities)
        {
            if (!profile.QualityOrder.Contains(allowed, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"Allowed quality '{allowed}' is missing from quality order.");
            }
        }

        if (!string.IsNullOrWhiteSpace(profile.UpgradeCutoffQuality) &&
            !profile.QualityOrder.Contains(profile.UpgradeCutoffQuality, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"Upgrade cutoff '{profile.UpgradeCutoffQuality}' is missing from quality order.");
        }

        foreach (var pattern in profile.RequiredRegex.Concat(profile.RejectedRegex))
        {
            ValidateRegex(pattern, errors);
        }

        var previousWait = -1;
        foreach (var tier in profile.FallbackTiers ?? [])
        {
            if (tier.AfterMinutes <= previousWait)
            {
                errors.Add("Fallback tiers must wait longer than the tier before them.");
            }

            previousWait = tier.AfterMinutes;
            foreach (var added in tier.AddedQualities ?? [])
            {
                if (!profile.QualityOrder.Contains(added, StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add($"Fallback quality '{added}' is missing from quality order.");
                }
            }
        }

        if (profile.UpgradeMinimumScoreDelta < 1 || profile.UpgradeMinimumQualitySteps < 1)
        {
            errors.Add("Upgrade benefit thresholds must be at least 1.");
        }

        foreach (var rule in profile.ScoreRules)
        {
            if (string.IsNullOrWhiteSpace(rule.Name))
            {
                errors.Add("Score rule name is required.");
            }

            if (string.IsNullOrWhiteSpace(rule.Value))
            {
                errors.Add($"Score rule '{rule.Name}' value is required.");
            }

            if (rule.Match == ReleaseRuleMatch.Regex)
            {
                ValidateRegex(rule.Value, errors);
            }
        }

        return errors;
    }

    private static bool Matches(
        ReleaseScoreRule rule,
        ReleaseInfo release)
    {
        var values = GetValues(rule.Field, release);
        return values.Any(value => rule.Match switch
        {
            ReleaseRuleMatch.Equals =>
                value.Equals(rule.Value, StringComparison.OrdinalIgnoreCase),
            ReleaseRuleMatch.Contains =>
                value.Contains(rule.Value, StringComparison.OrdinalIgnoreCase),
            ReleaseRuleMatch.Regex =>
                Regex.IsMatch(
                    value,
                    rule.Value,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    RegexTimeout),
            _ => false
        });
    }

    private static IEnumerable<string> GetValues(
        ReleaseRuleField field,
        ReleaseInfo release) =>
        field switch
        {
            ReleaseRuleField.RawTitle => [release.RawTitle],
            ReleaseRuleField.ReleaseGroup =>
                Value(release.ReleaseGroup),
            ReleaseRuleField.Source => [release.Source.ToString()],
            ReleaseRuleField.Resolution =>
                release.Resolution is int resolution ? [resolution.ToString()] : [],
            ReleaseRuleField.VideoCodec => [release.VideoCodec.ToString()],
            ReleaseRuleField.BitDepth =>
                release.BitDepth is int bitDepth ? [bitDepth.ToString()] : [],
            ReleaseRuleField.HdrFormat => [release.HdrFormat.ToString()],
            ReleaseRuleField.AudioCodec => [release.AudioCodec.ToString()],
            ReleaseRuleField.AudioLanguage => release.AudioLanguages,
            ReleaseRuleField.SubtitleLanguage => release.SubtitleLanguages,
            ReleaseRuleField.DualAudio => [release.IsDualAudio.ToString()],
            ReleaseRuleField.MultiAudio => [release.IsMultiAudio.ToString()],
            ReleaseRuleField.Proper => [release.IsProper.ToString()],
            ReleaseRuleField.Repack => [release.IsRepack.ToString()],
            _ => []
        };

    private static IEnumerable<string> Value(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : [value];

    private static int IndexOf(IReadOnlyList<string> values, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return int.MaxValue;
        }

        for (var index = 0; index < values.Count; index++)
        {
            if (values[index].Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    private static void ValidateRegex(string pattern, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            errors.Add("Regex pattern must not be empty.");
            return;
        }

        try
        {
            _ = new Regex(
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                RegexTimeout);
        }
        catch (ArgumentException)
        {
            errors.Add($"Invalid regex '{pattern}'.");
        }
    }
}
