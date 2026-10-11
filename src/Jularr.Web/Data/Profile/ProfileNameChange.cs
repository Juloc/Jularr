using Jularr.Data.Common;

namespace Jularr.Data.Profile;

public sealed record ProfileNameChange(
    [property: DataField(typeof(ProfileFields), nameof(ProfileFields.PublicId))] Guid PublicId,
    [property: DataField(typeof(ProfileFields), nameof(ProfileFields.DisplayName))] string DisplayName);
