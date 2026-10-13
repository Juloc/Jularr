using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Account;

namespace Jularr.Backend.Data.Logic.Session
{
    public sealed class SessionRenewalData : BaseData
    {
        public SessionRenewalData(DateTime expiresAt)
        {
            ExpiresAt = expiresAt;
        }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.ExpiresAt))]
        public DateTime ExpiresAt { get; }
    }
}
