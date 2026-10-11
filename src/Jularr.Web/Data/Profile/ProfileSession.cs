using Jularr.Data.Common;

namespace Jularr.Data.Profile;

public sealed record ProfileSession(long AccountId, long ProfileId, long OwnerAccountId);

public sealed record ProfileSessionParameters(Guid SessionPublicId, long SessionRevision);

public sealed record ProfileReadParameters(
    [property: DataField(typeof(ProfileFields), nameof(ProfileFields.PublicId))] Guid PublicId,
    Guid SessionPublicId,
    long SessionRevision);
