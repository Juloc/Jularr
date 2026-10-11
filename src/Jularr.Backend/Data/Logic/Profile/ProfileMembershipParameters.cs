using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Profile;
using Jularr.Backend.Data.Fields.Account;

namespace Jularr.Backend.Data.Logic.Profile
{
    public sealed class ProfileMembershipParameters : BaseData
    {
        public ProfileMembershipParameters(long accountId, long profileId)
        {
            AccountId = accountId;
            ProfileId = profileId;
        }

        [DataField(typeof(AccountFields), nameof(AccountFields.Id))]
        public long AccountId { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }
    }
}
