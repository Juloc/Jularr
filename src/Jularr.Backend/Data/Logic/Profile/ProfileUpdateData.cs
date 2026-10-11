using Jularr.Data.Common;
using Jularr.Data.Fields.Profile;

namespace Jularr.Data.Logic.Profile;

public sealed class ProfileUpdateData : BaseData
{
    public ProfileUpdateData(long id, string displayName, long uiLocaleId)
    {
        Id = id;
        DisplayName = displayName;
        UiLocaleId = uiLocaleId;
    }

    [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
    public long Id { get; internal set; }

    [DataField(typeof(ProfileFields), nameof(ProfileFields.DisplayName))]
    public string DisplayName { get; internal set; }

    [DataField(typeof(ProfileFields), nameof(ProfileFields.UiLocaleId))]
    public long UiLocaleId { get; internal set; }
}
