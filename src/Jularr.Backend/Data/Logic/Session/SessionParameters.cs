using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Session;

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

        [DataField(typeof(SessionFields), nameof(SessionFields.PublicId))]
        public Guid SessionPublicId { get; }

        [DataField(typeof(SessionFields), nameof(SessionFields.RotationRevision))]
        public long SessionRevision { get; }

        [DataField(typeof(SessionFields), nameof(SessionFields.TokenHash))]
        public byte[] SessionTokenHash { get; }
    }
}
