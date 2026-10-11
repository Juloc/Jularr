using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Account;
using Jularr.Backend.Data.Fields.Profile;

namespace Jularr.Backend.Data.Logic.Session
{
    public sealed class SessionAuthenticationData : BaseData
    {
        public SessionAuthenticationData(long accountId, long? profileId, long? profileOwnerAccountId)
        {
            AccountId = accountId;
            ProfileId = profileId;
            ProfileOwnerAccountId = profileOwnerAccountId;
        }

        [DataField(typeof(AccountFields), nameof(AccountFields.Id))]
        public long AccountId { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id), false)]
        public long? ProfileId { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.OwnerAccountId), false)]
        public long? ProfileOwnerAccountId { get; }
    }
}
