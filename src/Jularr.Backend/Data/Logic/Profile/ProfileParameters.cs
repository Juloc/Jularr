using Jularr.Data.Common;
using Jularr.Data.Fields.Profile;

namespace Jularr.Data.Logic.Profile;

public sealed class ProfileParameters : BaseData
{
    public ProfileParameters(long profileId)
    {
        ProfileId = profileId;
    }

    [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
    public long ProfileId { get; }
}
