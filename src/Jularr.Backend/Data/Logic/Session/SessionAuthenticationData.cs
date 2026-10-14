using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Account;
using Jularr.Backend.Data.Fields.Profile;
using Jularr.Backend.Data.Enums.Account;

namespace Jularr.Backend.Data.Logic.Session
{
    public sealed class SessionAuthenticationData : BaseData
    {
        public SessionAuthenticationData(long accountId, long? profileId, long? profileOwnerAccountId, long? uiLocaleId = null, AccountRoleType accountRoleTypeId = AccountRoleType.User,
            bool isDevice = false)
        {
            IsDevice = isDevice;
            AccountId = accountId;
            ProfileId = profileId;
            ProfileOwnerAccountId = profileOwnerAccountId;
            UiLocaleId = uiLocaleId;
            AccountRoleTypeId = accountRoleTypeId;
        }

        [DataField(typeof(AccountFields), nameof(AccountFields.Id))]
        public long AccountId { get; }

        [DataField(typeof(AccountFields), nameof(AccountFields.AccountRoleTypeId))]
        public AccountRoleType AccountRoleTypeId { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id), false)]
        public long? ProfileId { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.OwnerAccountId), false)]
        public long? ProfileOwnerAccountId { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.UiLocaleId), false)]
        public long? UiLocaleId { get; }

        // A paired TV, phone or app rather than a browser sign-in.
        public bool IsDevice { get; }
    }
}
