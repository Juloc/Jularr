using Jularr.Data.Common;

namespace Jularr.Data.User.Profile.V1;

// External requests use the opaque resource ID, never Profiles.Id bigint.
public sealed record ProfileParameters(Guid PublicId);

public sealed record RenameProfileData(string DisplayName);

public sealed record ProfileDetails(Guid PublicId, string DisplayName) : IServiceOutput;
