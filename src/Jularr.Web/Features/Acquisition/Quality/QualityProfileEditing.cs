using System.Globalization;

namespace Jularr.Web.Features.Acquisition.Quality;

/// <summary>
/// One Acquisition Profile as the Admin editor posts it: the ranked qualities in the order of their rows, one row per rule in the order of its
/// preference, and one term per line for the plain term lists. Nothing here is a second profile model, it is only the form of <see cref="QualityProfile"/>.
/// </summary>
public sealed class QualityProfileForm
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Quality keys, best first.</summary>
    public List<string> QualityOrder { get; set; } = [];

    /// <summary>The qualities taken at once; a profile that stores none takes every quality of the order, so the form shows them all.</summary>
    public List<string> AllowedQualities { get; set; } = [];

    public bool UpgradeAllowed { get; set; }

    public string? UpgradeCutoffQuality { get; set; }

    public string? UpgradeMinimumQualitySteps { get; set; }

    public string? MinimumSizeMegabytes { get; set; }

    public string? MaximumSizeMegabytes { get; set; }

    public bool AllowAmbiguousIdentity { get; set; }

    /// <summary>The indexer entries the profile may search, by their entry id; none ticked means every indexer takes part.</summary>
    public List<Guid> AllowedSources { get; set; } = [];

    /// <summary>The indexer entries whose releases win a tie; only entries the profile may search count.</summary>
    public List<Guid> PreferredSources { get; set; } = [];

    /// <summary>The indexer entries that are searched only when the other sources returned nothing.</summary>
    public List<Guid> FallbackOnlySources { get; set; } = [];

    public string MustContain { get; set; } = "";

    public string MustNotContain { get; set; } = "";

    public string RequiredRegex { get; set; } = "";

    public string RejectedRegex { get; set; } = "";

    /// <summary>The wanted languages, one per line, best first.</summary>
    public string LanguageOrder { get; set; } = "";

    public List<ScoreRuleRow> Rules { get; set; } = [];
}

/// <summary>One preference or gate as posted, every part as the owner chose or typed it.</summary>
public sealed class ScoreRuleRow
{
    public string? Effect { get; set; }

    public string? Field { get; set; }

    public string? Match { get; set; }

    public string? Value { get; set; }

    public string? Name { get; set; }

    /// <summary>An untouched row (the one the editor offers for adding a rule) is not a rule and not an error.</summary>
    public bool IsBlank => string.IsNullOrWhiteSpace(Value) && string.IsNullOrWhiteSpace(Name);
}

/// <param name="Field">The form field that is wrong.</param>
/// <param name="Line">The 1-based row of a list field (a rule), or null for a single value.</param>
/// <param name="Code">What is wrong: <c>number</c>, <c>rule</c>, <c>allowed</c> (no quality is taken at once) or <c>profile</c> (the profile as a whole is invalid; <paramref name="Detail"/> says why).</param>
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
            QualityOrder = [.. profile.QualityOrder],
            AllowedQualities = [.. profile.AllowedQualities.Length == 0 ? profile.QualityOrder : profile.AllowedQualities],
            UpgradeAllowed = profile.UpgradeAllowed,
            UpgradeCutoffQuality = profile.UpgradeCutoffQuality,
            UpgradeMinimumQualitySteps = profile.UpgradeMinimumQualitySteps.ToString(CultureInfo.InvariantCulture),
            MinimumSizeMegabytes = profile.MinimumSizeBytes is { } minimum ? (minimum / Megabyte).ToString(CultureInfo.InvariantCulture) : null,
            MaximumSizeMegabytes = profile.MaximumSizeBytes is { } maximum ? (maximum / Megabyte).ToString(CultureInfo.InvariantCulture) : null,
            AllowAmbiguousIdentity = profile.AllowAmbiguousIdentity,
            AllowedSources = [.. profile.SourcePolicy.AllowedEntryIds],
            PreferredSources = [.. profile.SourcePolicy.PreferredEntryIds],
            FallbackOnlySources = [.. profile.SourcePolicy.FallbackOnlyEntryIds],
            MustContain = string.Join('\n', profile.MustContain),
            MustNotContain = string.Join('\n', profile.MustNotContain),
            RequiredRegex = string.Join('\n', profile.RequiredRegex),
            RejectedRegex = string.Join('\n', profile.RejectedRegex),
            LanguageOrder = string.Join('\n', profile.LanguageOrder),
            Rules = [.. InPreferenceOrder(profile.ScoreRules).Select(ToRow)]
        };

    private static ScoreRuleRow ToRow(ReleaseScoreRule rule) =>
        new()
        {
            Effect = rule.EffectiveEffect.ToString(),
            Field = rule.Field.ToString(),
            Match = rule.Match.ToString(),
            Value = rule.Value,
            Name = rule.Name
        };

    /// <summary>
    /// Reads the form into a profile. Every problem is reported with its field and line, and the profile is only returned when it also passes the
    /// scorer's own validation, so a stored profile can always be used to score a release.
    /// </summary>
    public static ProfileEditResult Parse(QualityProfileForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        var errors = new List<ProfileEditError>();
        var order = Distinct(form.QualityOrder);
        var allowed = Distinct(form.AllowedQualities);
        if (order.Length > 0 && allowed.Length == 0)
        {
            // A profile that stores no allowed quality takes every one, so "none ticked" must not silently turn into "all".
            errors.Add(new ProfileEditError(nameof(QualityProfileForm.AllowedQualities), null, "allowed"));
        }

        var rules = new List<ReleaseScoreRule>();
        var ruleRows = form.Rules.Where(row => !row.IsBlank).ToArray();
        for (var index = 0; index < ruleRows.Length; index++)
        {
            var rule = ParseRule(ruleRows[index], index + 1);
            if (rule is null)
            {
                errors.Add(new ProfileEditError(nameof(QualityProfileForm.Rules), index + 1, "rule"));
                continue;
            }

            rules.Add(rule);
        }

        var steps = Number(form.UpgradeMinimumQualitySteps, 1, nameof(QualityProfileForm.UpgradeMinimumQualitySteps), errors);
        var minimumSize = Optional(form.MinimumSizeMegabytes, nameof(QualityProfileForm.MinimumSizeMegabytes), errors);
        var maximumSize = Optional(form.MaximumSizeMegabytes, nameof(QualityProfileForm.MaximumSizeMegabytes), errors);
        var cutoff = string.IsNullOrWhiteSpace(form.UpgradeCutoffQuality) ? null : form.UpgradeCutoffQuality.Trim();
        if (rules.Count(rule => rule.EffectiveEffect is ReleaseRuleEffect.Prefer or ReleaseRuleEffect.Avoid) > MaxPreferenceRules)
        {
            errors.Add(new ProfileEditError(nameof(QualityProfileForm.Rules), null, "rule"));
        }

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
            minimumSize is { } minimumMegabytes ? minimumMegabytes * Megabyte : null,
            maximumSize is { } maximumMegabytes ? maximumMegabytes * Megabyte : null,
            Lines(form.MustContain),
            Lines(form.MustNotContain),
            Lines(form.RequiredRegex),
            Lines(form.RejectedRegex),
            [.. WithOrderedWeights(rules)])
        {
            UpgradeMinimumQualitySteps = steps,
            AllowAmbiguousIdentity = form.AllowAmbiguousIdentity,
            LanguageOrder = [.. Lines(form.LanguageOrder).Select(language => language.ToLowerInvariant()).Distinct()],
            SourcePolicy = SourcePolicyOf(form)
        };
        foreach (var problem in ReleaseScorer.ValidateProfile(profile))
        {
            errors.Add(new ProfileEditError("Profile", null, "profile", problem));
        }

        return errors.Count == 0 ? new ProfileEditResult(profile, []) : new ProfileEditResult(null, errors);
    }

    /// <summary>A preferred or fallback-only source the profile may not search could never be asked, so it is not stored.</summary>
    private static AcquisitionSourcePolicy SourcePolicyOf(QualityProfileForm form)
    {
        var allowed = form.AllowedSources.Distinct().Order().ToArray();
        var preferred = form.PreferredSources.Distinct().Where(id => allowed.Length == 0 || allowed.Contains(id)).Order().ToArray();
        var fallbackOnly = form.FallbackOnlySources.Distinct().Where(id => allowed.Length == 0 || allowed.Contains(id)).Order().ToArray();
        return new AcquisitionSourcePolicy(allowed, preferred) { FallbackOnlyEntryIds = fallbackOnly } is { IsDefault: false } policy ? policy : AcquisitionSourcePolicy.Unrestricted;
    }

    /// <summary>The most Prefer and Avoid rules one profile holds: each takes a power of two as its weight, so an earlier rule always outweighs every later one.</summary>
    public const int MaxPreferenceRules = 30;

    // The rows the editor shows: gates in their stored order, then the preferences strongest first and the avoided traits strongest first.
    private static IEnumerable<ReleaseScoreRule> InPreferenceOrder(IEnumerable<ReleaseScoreRule> rules)
    {
        var all = rules.ToArray();
        return all.Where(rule => rule.EffectiveEffect is not (ReleaseRuleEffect.Prefer or ReleaseRuleEffect.Avoid))
            .Concat(all.Where(rule => rule.EffectiveEffect == ReleaseRuleEffect.Prefer).OrderByDescending(rule => rule.Score))
            .Concat(all.Where(rule => rule.EffectiveEffect == ReleaseRuleEffect.Avoid).OrderBy(rule => rule.Score));
    }

    // The order of the rows is the whole preference: a Prefer rule above another outweighs it and everything below, every Prefer outweighs all Avoid rules.
    private static IEnumerable<ReleaseScoreRule> WithOrderedWeights(IReadOnlyList<ReleaseScoreRule> rules)
    {
        var avoids = rules.Count(rule => rule.EffectiveEffect == ReleaseRuleEffect.Avoid);
        var prefers = rules.Count(rule => rule.EffectiveEffect == ReleaseRuleEffect.Prefer);
        var preferIndex = 0;
        var avoidIndex = 0;
        foreach (var rule in rules)
        {
            yield return rule.EffectiveEffect switch
            {
                ReleaseRuleEffect.Prefer => rule with { Score = 1 << (avoids + prefers - 1 - preferIndex++) },
                ReleaseRuleEffect.Avoid => rule with { Score = -(1 << (avoids - 1 - avoidIndex++)) },
                _ => rule with { Score = 0 }
            };
        }
    }

    private static ReleaseScoreRule? ParseRule(ScoreRuleRow row, int number)
    {
        if (!Enum.TryParse<ReleaseRuleEffect>(row.Effect, ignoreCase: true, out var effect)
            || !Enum.TryParse<ReleaseRuleField>(row.Field, ignoreCase: true, out var field)
            || !Enum.TryParse<ReleaseRuleMatch>(row.Match, ignoreCase: true, out var match)
            || string.IsNullOrWhiteSpace(row.Value))
        {
            return null;
        }

        var name = string.IsNullOrWhiteSpace(row.Name) ? $"Rule {number}" : row.Name.Trim();
        return new ReleaseScoreRule(name, field, match, row.Value.Trim(), 0) { Effect = effect };
    }

    private static string[] Distinct(IEnumerable<string>? values) =>
        [.. (values ?? []).Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

    private static string[] Lines(string? text) =>
        (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int Number(string? text, int fallback, string field, List<ProfileEditError> errors)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 1)
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
