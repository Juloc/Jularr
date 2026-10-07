using System.Globalization;

namespace Jularr.Web.Features.Acquisition.Quality;

/// <summary>
/// The editable text of one Acquisition Profile as the Admin editor posts it. Lists are one entry per line so the order the owner types is the
/// order the engine ranks by; nothing here is a second profile model, it is only the form of <see cref="QualityProfile"/>.
/// </summary>
public sealed class QualityProfileForm
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Quality keys, best first.</summary>
    public string QualityOrder { get; set; } = "";

    /// <summary>The qualities taken at once; empty means every quality of the order.</summary>
    public string AllowedQualities { get; set; } = "";

    /// <summary>One fallback step per line, <c>minutes: quality, quality</c>: after that wait the qualities are allowed too and taken as temporary.</summary>
    public string FallbackTiers { get; set; } = "";

    public bool UpgradeAllowed { get; set; }

    public string? UpgradeCutoffQuality { get; set; }

    public string? UpgradeMinimumQualitySteps { get; set; }

    public string? UpgradeMinimumScoreDelta { get; set; }

    public string? UpgradeUntilScore { get; set; }

    public string? MinimumScore { get; set; }

    public string? MinimumSizeMegabytes { get; set; }

    public string? MaximumSizeMegabytes { get; set; }

    public bool AllowAmbiguousIdentity { get; set; }

    public string MustContain { get; set; } = "";

    public string MustNotContain { get; set; } = "";

    public string RequiredRegex { get; set; } = "";

    public string RejectedRegex { get; set; } = "";

    /// <summary>One rule per line, <c>Effect | Field | Match | Value | Score | Name</c>, for example <c>Prefer | ReleaseGroup | Equals | GRP | 20 | Preferred group</c>.</summary>
    public string ScoreRules { get; set; } = "";
}

/// <param name="Field">The form field that is wrong.</param>
/// <param name="Line">The 1-based line of a list field, or null for a single value.</param>
/// <param name="Code">What is wrong: <c>number</c>, <c>rule</c>, <c>tier</c>, <c>quality</c> or <c>profile</c> (the profile as a whole is invalid; <paramref name="Detail"/> says why).</param>
public sealed record ProfileEditError(string Field, int? Line, string Code, string? Detail = null);

public sealed record ProfileEditResult(QualityProfile? Profile, IReadOnlyList<ProfileEditError> Errors)
{
    public bool IsValid => Profile is not null && Errors.Count == 0;
}

/// <summary>Translates between <see cref="QualityProfile"/> and its editable text, and validates what the owner typed before anything is stored.</summary>
public static class QualityProfileEditing
{
    private const long Megabyte = 1024 * 1024;

    public static QualityProfileForm ToForm(QualityProfile profile) =>
        new()
        {
            Id = profile.Id,
            Name = profile.Name,
            QualityOrder = string.Join('\n', profile.QualityOrder),
            AllowedQualities = string.Join('\n', profile.AllowedQualities),
            FallbackTiers = string.Join('\n', profile.FallbackTiers.Select(tier => $"{tier.AfterMinutes}: {string.Join(", ", tier.AddedQualities)}")),
            UpgradeAllowed = profile.UpgradeAllowed,
            UpgradeCutoffQuality = profile.UpgradeCutoffQuality,
            UpgradeMinimumQualitySteps = profile.UpgradeMinimumQualitySteps.ToString(CultureInfo.InvariantCulture),
            UpgradeMinimumScoreDelta = profile.UpgradeMinimumScoreDelta.ToString(CultureInfo.InvariantCulture),
            UpgradeUntilScore = profile.UpgradeUntilScore?.ToString(CultureInfo.InvariantCulture),
            MinimumScore = profile.MinimumScore.ToString(CultureInfo.InvariantCulture),
            MinimumSizeMegabytes = profile.MinimumSizeBytes is { } minimum ? (minimum / Megabyte).ToString(CultureInfo.InvariantCulture) : null,
            MaximumSizeMegabytes = profile.MaximumSizeBytes is { } maximum ? (maximum / Megabyte).ToString(CultureInfo.InvariantCulture) : null,
            AllowAmbiguousIdentity = profile.AllowAmbiguousIdentity,
            MustContain = string.Join('\n', profile.MustContain),
            MustNotContain = string.Join('\n', profile.MustNotContain),
            RequiredRegex = string.Join('\n', profile.RequiredRegex),
            RejectedRegex = string.Join('\n', profile.RejectedRegex),
            ScoreRules = string.Join('\n', profile.ScoreRules.Select(rule => $"{rule.EffectiveEffect} | {rule.Field} | {rule.Match} | {rule.Value} | {rule.Score} | {rule.Name}"))
        };

    /// <summary>
    /// Reads the form into a profile. Every problem is reported with its field and line, and the profile is only returned when it also passes the
    /// scorer's own validation, so a stored profile can always be used to score a release.
    /// </summary>
    public static ProfileEditResult Parse(QualityProfileForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        var errors = new List<ProfileEditError>();
        var order = Lines(form.QualityOrder);
        var allowed = Lines(form.AllowedQualities);
        var tiers = new List<FallbackTier>();
        var tierLines = Lines(form.FallbackTiers);
        for (var index = 0; index < tierLines.Length; index++)
        {
            var separator = tierLines[index].IndexOf(':');
            var qualities = separator < 0 ? [] : tierLines[index][(separator + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (separator < 0 || !int.TryParse(tierLines[index][..separator].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes < 1 || qualities.Length == 0)
            {
                errors.Add(new ProfileEditError(nameof(QualityProfileForm.FallbackTiers), index + 1, "tier"));
                continue;
            }

            tiers.Add(new FallbackTier(minutes, qualities));
        }

        var rules = new List<ReleaseScoreRule>();
        var ruleLines = Lines(form.ScoreRules);
        for (var index = 0; index < ruleLines.Length; index++)
        {
            var rule = ParseRule(ruleLines[index], index + 1);
            if (rule is null)
            {
                errors.Add(new ProfileEditError(nameof(QualityProfileForm.ScoreRules), index + 1, "rule"));
                continue;
            }

            rules.Add(rule);
        }

        var steps = Number(form.UpgradeMinimumQualitySteps, 1, nameof(QualityProfileForm.UpgradeMinimumQualitySteps), errors);
        var delta = Number(form.UpgradeMinimumScoreDelta, 1, nameof(QualityProfileForm.UpgradeMinimumScoreDelta), errors);
        var until = Optional(form.UpgradeUntilScore, nameof(QualityProfileForm.UpgradeUntilScore), errors);
        var minimumScore = Number(form.MinimumScore, 0, nameof(QualityProfileForm.MinimumScore), errors, allowNegative: true);
        var minimumSize = Optional(form.MinimumSizeMegabytes, nameof(QualityProfileForm.MinimumSizeMegabytes), errors);
        var maximumSize = Optional(form.MaximumSizeMegabytes, nameof(QualityProfileForm.MaximumSizeMegabytes), errors);
        var cutoff = string.IsNullOrWhiteSpace(form.UpgradeCutoffQuality) ? null : form.UpgradeCutoffQuality.Trim();
        if (errors.Count > 0)
        {
            return new ProfileEditResult(null, errors);
        }

        var profile = new QualityProfile(
            form.Id.Trim(),
            form.Name.Trim(),
            allowed,
            order,
            form.UpgradeAllowed,
            cutoff,
            minimumScore,
            minimumSize is { } minimumMegabytes ? minimumMegabytes * Megabyte : null,
            maximumSize is { } maximumMegabytes ? maximumMegabytes * Megabyte : null,
            Lines(form.MustContain),
            Lines(form.MustNotContain),
            Lines(form.RequiredRegex),
            Lines(form.RejectedRegex),
            [.. rules])
        {
            FallbackTiers = [.. tiers.OrderBy(tier => tier.AfterMinutes)],
            UpgradeMinimumQualitySteps = steps,
            UpgradeMinimumScoreDelta = delta,
            UpgradeUntilScore = until,
            AllowAmbiguousIdentity = form.AllowAmbiguousIdentity
        };
        foreach (var problem in ReleaseScorer.ValidateProfile(profile))
        {
            errors.Add(new ProfileEditError("Profile", null, "profile", problem));
        }

        foreach (var tier in profile.FallbackTiers)
        {
            foreach (var quality in tier.AddedQualities.Where(quality => !order.Contains(quality, StringComparer.OrdinalIgnoreCase)))
            {
                errors.Add(new ProfileEditError(nameof(QualityProfileForm.FallbackTiers), null, "quality", quality));
            }
        }

        return errors.Count == 0 ? new ProfileEditResult(profile, []) : new ProfileEditResult(null, errors);
    }

    private static ReleaseScoreRule? ParseRule(string line, int number)
    {
        var parts = line.Split('|', 6, StringSplitOptions.TrimEntries);
        if (parts.Length < 5
            || !Enum.TryParse<ReleaseRuleEffect>(parts[0], ignoreCase: true, out var effect)
            || !Enum.TryParse<ReleaseRuleField>(parts[1], ignoreCase: true, out var field)
            || !Enum.TryParse<ReleaseRuleMatch>(parts[2], ignoreCase: true, out var match)
            || string.IsNullOrWhiteSpace(parts[3])
            || !int.TryParse(parts[4], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var score))
        {
            return null;
        }

        // A rule of a gate effect has no score of its own: it only keeps a release in or out.
        if (effect is ReleaseRuleEffect.Require or ReleaseRuleEffect.Reject or ReleaseRuleEffect.Info)
        {
            score = 0;
        }

        return new ReleaseScoreRule(parts.Length > 5 && parts[5].Length > 0 ? parts[5] : $"Rule {number}", field, match, parts[3], score) { Effect = effect };
    }

    private static string[] Lines(string? text) =>
        (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int Number(string? text, int fallback, string field, List<ProfileEditError> errors, bool allowNegative = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (int.TryParse(text.Trim(), allowNegative ? NumberStyles.AllowLeadingSign : NumberStyles.None, CultureInfo.InvariantCulture, out var value) && (allowNegative || value >= 1))
        {
            return value;
        }

        errors.Add(new ProfileEditError(field, null, "number"));
        return fallback;
    }

    private static int? Optional(string? text, string field, List<ProfileEditError> errors)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        errors.Add(new ProfileEditError(field, null, "number"));
        return null;
    }
}
