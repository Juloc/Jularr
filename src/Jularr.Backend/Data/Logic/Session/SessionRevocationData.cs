using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Account;

namespace Jularr.Backend.Data.Logic.Session
{
    public sealed class SessionRevocationData : BaseData
    {
        public SessionRevocationData(Guid sessionPublicId, long accountId)
        {
            SessionPublicId = sessionPublicId;
            AccountId = accountId;
        }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.PublicId))]
        public Guid SessionPublicId { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.AccountId))]
        public long AccountId { get; }
    }
}
