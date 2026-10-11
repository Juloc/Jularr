namespace Jularr.Data.Profile;

// Neutral, unversioned Logic input. No User/Admin/System DTO dependencies.
public sealed record ProfileNameChange(Guid PublicId, string DisplayName);
