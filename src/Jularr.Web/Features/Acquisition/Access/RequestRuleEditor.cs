using Jularr.Web.Features.Localization;

namespace Jularr.Web.Features.Acquisition.Access;

public sealed class RequestRuleEditorInput
{
    public long? Id { get; set; }
    public long? RuleId { get; set; }
    public long Revision { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Unlimited { get; set; }
    public int Limit { get; set; } = 10;
    public int PeriodDays { get; set; } = 30;
    public RequestApprovalMode Approval { get; set; }
    public string[] Kinds { get; set; } = [];
    public bool UseOverrides { get; set; }

    public RequestRuleValues ReadValues(IReadOnlyList<MediaAcquisitionKind> editableKinds, IReadOnlyList<MediaAcquisitionKind> preservedKinds)
    {
        var kinds = Kinds.Select(AcquisitionAccessNames.ParseKind).Distinct().ToArray();
        if (kinds.Any(kind => !editableKinds.Contains(kind)))
        {
            throw new ArgumentException("A disabled or forbidden media type cannot be selected.");
        }

        var retained = preservedKinds.Where(kind => !editableKinds.Contains(kind));
        return new RequestRuleValues(Unlimited ? null : Limit, PeriodDays, Approval, [.. kinds, .. retained]).Validate();
    }

    public static RequestRuleEditorInput FromProfile(RequestRuleProfile profile, long revision, ResolvedRequestRule? user = null) => new()
    {
        Id = profile.Id,
        RuleId = user?.InheritsDefault is false ? profile.Id : null,
        Revision = revision,
        Name = profile.Name,
        Description = profile.Description,
        Unlimited = (user?.Values ?? profile.Values).Limit is null,
        Limit = (user?.Values ?? profile.Values).Limit ?? 10,
        PeriodDays = (user?.Values ?? profile.Values).PeriodDays,
        Approval = (user?.Values ?? profile.Values).Approval,
        Kinds = (user?.Values ?? profile.Values).Kinds.Select(AcquisitionAccessNames.Kind).ToArray(),
        UseOverrides = user?.Overrides.HasChanges is true
    };
}

public sealed record RequestRuleEditorView(UiTextBundle Ui, RequestRuleEditorInput Editor, AcquisitionRequestSettings Settings,
    IReadOnlyList<MediaAcquisitionKind> EnabledKinds, IReadOnlyList<MediaAcquisitionKind> EditableKinds, string? UserName = null, string? UserId = null);
