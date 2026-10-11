using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Profile;

namespace Jularr.Backend.Data.Logic.Profile
{
    public sealed class ProfileParameters : BaseData
    {
        public ProfileParameters(long profileId)
        {
            ProfileId = profileId;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }
    }
}
