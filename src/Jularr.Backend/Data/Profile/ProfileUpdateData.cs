using Jularr.Data.Common;

namespace Jularr.Data.Profile;

public sealed class ProfileUpdateData(long id, Guid publicId, string displayName)
{
    [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
    public long Id { get; } = id;

    [DataField(typeof(ProfileFields), nameof(ProfileFields.PublicId))]
    public Guid PublicId { get; } = publicId;

    [DataField(typeof(ProfileFields), nameof(ProfileFields.DisplayName))]
    public string DisplayName { get; internal set; } = displayName;
}
