namespace Jularr.Web.Features.Acquisition.Access;

/// <summary>At most <see cref="MaxRequests"/> auto-approved requests per requester within <see cref="PeriodDays"/> days.</summary>
public sealed record AutoApprovalQuota(int MaxRequests, int PeriodDays)
{
    public const int MaxRequestsLimit = 1000;
    public const int PeriodDaysLimit = 365;

    public TimeSpan Period => TimeSpan.FromDays(PeriodDays);
}

/// <summary>
/// One auto-approval rule: requests it matches skip the owner's queue. A request matches when the
/// rule is enabled, its media type is one of <see cref="Kinds"/> (empty means every media type) and
/// its requester is one of <see cref="ProfileIds"/> (empty means every requester). A rule with a
/// <see cref="Quota"/> only approves while the requester has quota left; further requests wait for
/// the owner. The blanket "add immediately" mode is the <c>Instant</c> capability, not a rule.
/// </summary>
public sealed record AutoApprovalRule(
    string Id,
    string Name,
    bool Enabled,
    IReadOnlyList<MediaAcquisitionKind> Kinds,
    IReadOnlyList<string> ProfileIds,
    AutoApprovalQuota? Quota)
{
    public const int MaxNameLength = 60;

    public bool Matches(MediaAcquisitionKind kind, string profileId) =>
        Enabled
        && (Kinds.Count == 0 || Kinds.Contains(kind))
        && (ProfileIds.Count == 0 || ProfileIds.Contains(profileId, StringComparer.Ordinal));

    /// <summary>Builds a new rule with a fresh id from the owner's input, or throws <see cref="ArgumentException"/>.</summary>
    public static AutoApprovalRule Create(
        string? name,
        IEnumerable<MediaAcquisitionKind> kinds,
        IEnumerable<string> profileIds,
        AutoApprovalQuota? quota)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException($"A rule needs a name of up to {MaxNameLength} characters.", nameof(name));
        }

        if (quota is not null
            && (quota.MaxRequests is < 1 or > AutoApprovalQuota.MaxRequestsLimit
                || quota.PeriodDays is < 1 or > AutoApprovalQuota.PeriodDaysLimit))
        {
            throw new ArgumentException("The quota is out of range.", nameof(quota));
        }

        return new AutoApprovalRule(
            Guid.NewGuid().ToString("N")[..12],
            trimmed,
            Enabled: true,
            [.. kinds.Distinct().Order()],
            [.. profileIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.Ordinal)],
            quota);
    }
}

/// <summary>
/// The owner's request settings that are configuration and not relational data (JSON store, like the
/// capability matrix): request rule profiles, sparse user assignments, preserved approval rules and requester-selectable quality profiles.
/// </summary>
public sealed record AcquisitionRequestSettings(
    IReadOnlyList<AutoApprovalRule> AutoApprovalRules,
    IReadOnlyList<string> RequesterQualityProfileIds)
{
    public static AcquisitionRequestSettings Default { get; } = new([], []);
    public RequestRuleConfiguration Rules { get; init; } = RequestRuleConfiguration.Standard;
    public long Revision { get; init; }
}

/// <summary>What the rules decide for one new request.</summary>
public sealed record AutoApprovalDecision(bool AutoApprove, AutoApprovalRule? Rule)
{
    public static AutoApprovalDecision NeedsApproval { get; } = new(false, null);
}

/// <summary>How an auto-approval is recorded on the request, so history can tell it from a person's decision.</summary>
public static class AcquisitionAutoApproval
{
    private const string Prefix = "auto-approval:";

    public static string DecidedBy(string ruleId) => Prefix + ruleId;

    public static bool TryParseRuleId(string? decidedBy, out string ruleId)
    {
        if (decidedBy is not null && decidedBy.StartsWith(Prefix, StringComparison.Ordinal) && decidedBy.Length > Prefix.Length)
        {
            ruleId = decidedBy[Prefix.Length..];
            return true;
        }

        ruleId = string.Empty;
        return false;
    }
}

public static class AutoApprovalEvaluator
{
    /// <summary>The enabled rules that match and are limited by a quota — their usage has to be counted before <see cref="Evaluate"/>.</summary>
    public static IReadOnlyList<AutoApprovalRule> QuotaRulesFor(
        IEnumerable<AutoApprovalRule> rules,
        MediaAcquisitionKind kind,
        string profileId) =>
        [.. rules.Where(rule => rule.Quota is not null && rule.Matches(kind, profileId))];

    /// <summary>
    /// The first matching rule that still has quota approves the request. <paramref name="usedByRule"/> is
    /// how many requests of this requester the rule already approved within its period (missing = none).
    /// No matching rule, or every matching rule out of quota, leaves the request for the owner.
    /// </summary>
    public static AutoApprovalDecision Evaluate(
        IEnumerable<AutoApprovalRule> rules,
        MediaAcquisitionKind kind,
        string profileId,
        IReadOnlyDictionary<string, int> usedByRule)
    {
        foreach (var rule in rules)
        {
            if (!rule.Matches(kind, profileId))
            {
                continue;
            }

            if (rule.Quota is { } quota && usedByRule.GetValueOrDefault(rule.Id) >= quota.MaxRequests)
            {
                continue;
            }

            return new AutoApprovalDecision(true, rule);
        }

        return AutoApprovalDecision.NeedsApproval;
    }
}
