using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Account;

namespace Jularr.Backend.Data.Logic.Account
{
    public sealed class AccountParameters : BaseData
    {
        public AccountParameters(long accountId)
        {
            AccountId = accountId;
        }

        [DataField(typeof(AccountFields), nameof(AccountFields.Id))]
        public long AccountId { get; }
    }
}
