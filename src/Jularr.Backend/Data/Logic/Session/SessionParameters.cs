using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Account;

namespace Jularr.Backend.Data.Logic.Session
{
    public sealed class SessionParameters : BaseData
    {
        public SessionParameters(Guid sessionPublicId, long sessionRevision, byte[] sessionTokenHash)
        {
            SessionPublicId = sessionPublicId;
            SessionRevision = sessionRevision;
            SessionTokenHash = sessionTokenHash;
        }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.PublicId))]
        public Guid SessionPublicId { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.RotationRevision))]
        public long SessionRevision { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.TokenHash))]
        public byte[] SessionTokenHash { get; }
    }
}
