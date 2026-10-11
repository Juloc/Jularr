using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Session;

namespace Jularr.Backend.Data.Logic.Session
{
    public sealed class SessionAccountParameters : BaseData
    {
        public SessionAccountParameters(long accountId)
        {
            AccountId = accountId;
        }

        [DataField(typeof(SessionFields), nameof(SessionFields.AccountId))]
        public long AccountId { get; }
    }
}
