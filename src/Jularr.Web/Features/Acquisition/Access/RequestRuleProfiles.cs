namespace Jularr.Web.Features.Acquisition.Access;

public enum RequestApprovalMode
{
    Manual,
    Automatic
}

// A missing quality list identifies older saved profiles; the settings owner upgrades it before validating to an explicit list.
public sealed record RequestRuleValues(int? Limit, int PeriodDays, RequestApprovalMode Approval, IReadOnlyList<MediaAcquisitionKind> Kinds, IReadOnlyList<string>? QualityProfileIds = null)
{
    public static RequestRuleValues Standard { get; } = new(10, 30, RequestApprovalMode.Manual, Enum.GetValues<MediaAcquisitionKind>());

    public RequestRuleValues Validate()
    {
        if (Limit is < 1 or > 10000 || PeriodDays is < 1 or > 365 || !Enum.IsDefined(Approval) || Kinds is null || Kinds.Any(kind => !Enum.IsDefined(kind)))
        {
            throw new ArgumentException("Invalid request rule values.");
        }

        var qualityIds = QualityProfileIds ?? [];
        if (qualityIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Quality profile identities cannot be empty.");
        }

        return this with { Kinds = Kinds.Distinct().Order().ToArray(), QualityProfileIds = qualityIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() };
    }
}

public sealed record RequestRuleProfile(long Id, string Name, string? Description, RequestRuleValues Values)
{
    public RequestRuleProfile Validate()
    {
        var name = Name?.Trim() ?? string.Empty;
        var description = Description?.Trim();
        if (Id < 1 || name.Length is < 1 or > 60 || description?.Length > 500 || Values is null)
        {
            throw new ArgumentException("A request rule needs a name of up to 60 characters and a description of up to 500 characters.");
        }

        return this with { Name = name, Description = string.IsNullOrEmpty(description) ? null : description, Values = Values.Validate() };
    }
}

// Limit's presence flag distinguishes unlimited from inheritance. Null collections inherit; empty ones explicitly clear the choices.
public sealed record RequestRuleOverrides(bool HasLimit = false, int? Limit = null, int? PeriodDays = null, RequestApprovalMode? Approval = null,
    IReadOnlyList<MediaAcquisitionKind>? Kinds = null, IReadOnlyList<string>? QualityProfileIds = null)
{
    public bool HasChanges => HasLimit || PeriodDays is not null || Approval is not null || Kinds is not null || QualityProfileIds is not null;

    public RequestRuleValues Apply(RequestRuleValues inherited) => new(HasLimit ? Limit : inherited.Limit, PeriodDays ?? inherited.PeriodDays, Approval ?? inherited.Approval,
        Kinds ?? inherited.Kinds, QualityProfileIds ?? inherited.QualityProfileIds);

    public static RequestRuleOverrides Difference(RequestRuleValues edited, RequestRuleValues inherited)
    {
        edited = edited.Validate();
        inherited = inherited.Validate();
        return new(edited.Limit != inherited.Limit, edited.Limit == inherited.Limit ? null : edited.Limit, edited.PeriodDays == inherited.PeriodDays ? null : edited.PeriodDays,
            edited.Approval == inherited.Approval ? null : edited.Approval, edited.Kinds.Order().SequenceEqual(inherited.Kinds.Order()) ? null : edited.Kinds,
            edited.QualityProfileIds!.SequenceEqual(inherited.QualityProfileIds!, StringComparer.Ordinal) ? null : edited.QualityProfileIds);
    }
}

public sealed record UserRequestRule(long? RuleId, RequestRuleOverrides Overrides);
public sealed record ResolvedRequestRule(RequestRuleProfile Profile, bool InheritsDefault, RequestRuleOverrides Overrides, RequestRuleValues Values, IReadOnlyList<AutoApprovalRule> TransitionRules);

public sealed record RequestRuleConfiguration(long DefaultId, long NextId, IReadOnlyList<RequestRuleProfile> Profiles, IReadOnlyDictionary<string, UserRequestRule> Users, bool HasApprovalTransition = false)
{
    public static RequestRuleConfiguration Standard { get; } = new(1, 2, [new(1, "Standard", null, RequestRuleValues.Standard)], new Dictionary<string, UserRequestRule>());

    public ResolvedRequestRule Resolve(string userId, IReadOnlyList<AutoApprovalRule> approvalRules)
    {
        Users.TryGetValue(userId, out var assignment);
        var profile = Profiles.Single(profile => profile.Id == (assignment?.RuleId ?? DefaultId));
        var overrides = assignment?.Overrides ?? new RequestRuleOverrides();
        var transition = HasApprovalTransition && assignment?.RuleId is null && !overrides.HasChanges ? approvalRules : [];
        return new(profile, assignment?.RuleId is null, overrides, overrides.Apply(profile.Values), transition);
    }

    public RequestRuleConfiguration Validate()
    {
        if (Profiles is null || Users is null || Profiles.Count == 0 || Profiles.Count > 200 || Profiles.Any(profile => profile is null) || Profiles.Select(profile => profile.Id).Distinct().Count() != Profiles.Count
            || Profiles.Count(profile => profile.Id == DefaultId) != 1 || NextId <= Profiles.Max(profile => profile.Id))
        {
            throw new InvalidDataException("Request rule profiles have invalid identities or no unique default.");
        }

        var profiles = Profiles.Select(profile => profile.Validate()).ToArray();
        foreach (var (userId, assignment) in Users)
        {
            if (string.IsNullOrWhiteSpace(userId) || assignment is null || assignment.Overrides is null || assignment.RuleId is { } id && !profiles.Any(profile => profile.Id == id))
            {
                throw new InvalidDataException("A request rule assignment refers to an unknown profile.");
            }

            assignment.Overrides.Apply(profiles.Single(profile => profile.Id == (assignment.RuleId ?? DefaultId)).Values).Validate();
        }

        return this with { Profiles = profiles };
    }
}

public sealed class RequestRuleConflictException() : InvalidOperationException("Request rules changed in another session. Reload before saving.");
