using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Session;

namespace Jularr.Backend.Data.Logic.Session
{
    public sealed class SessionRevocationData : BaseData
    {
        public SessionRevocationData(Guid sessionPublicId, long accountId)
        {
            SessionPublicId = sessionPublicId;
            AccountId = accountId;
        }

        [DataField(typeof(SessionFields), nameof(SessionFields.PublicId))]
        public Guid SessionPublicId { get; }

        [DataField(typeof(SessionFields), nameof(SessionFields.AccountId))]
        public long AccountId { get; }
    }
}
