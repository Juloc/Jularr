using Jularr.Data.Common;
using Jularr.Data.Profile;

namespace Jularr.Data.User.Profile.V1;

public sealed record ProfileParameters(
    [property: DataField(typeof(ProfileFields), nameof(ProfileFields.PublicId))] Guid PublicId);

public sealed record UpdateProfileDataV1(
    [property: DataField(typeof(ProfileFields), nameof(ProfileFields.DisplayName))] string DisplayName);

public sealed record ProfileDetails(
    [property: DataField(typeof(ProfileFields), nameof(ProfileFields.PublicId))] Guid PublicId,
    [property: DataField(typeof(ProfileFields), nameof(ProfileFields.DisplayName))] string DisplayName) : IServiceOutput;
