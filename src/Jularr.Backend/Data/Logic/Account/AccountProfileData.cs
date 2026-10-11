using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Account;

namespace Jularr.Backend.Data.Logic.Account
{
    public sealed class AccountProfileData : BaseData
    {
        public AccountProfileData(long accountId, long profileId)
        {
            AccountId = accountId;
            ProfileId = profileId;
        }

        [DataField(typeof(AccountProfileFields), nameof(AccountProfileFields.AccountId))]
        public long AccountId { get; }

        [DataField(typeof(AccountProfileFields), nameof(AccountProfileFields.ProfileId))]
        public long ProfileId { get; }
    }
}
