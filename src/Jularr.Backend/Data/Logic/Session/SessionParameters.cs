using Jularr.Data.Common;
using Jularr.Data.Fields.Session;

namespace Jularr.Data.Logic.Session;

public sealed class SessionParameters : BaseData
{
    public SessionParameters(Guid sessionPublicId, long sessionRevision)
    {
        SessionPublicId = sessionPublicId;
        SessionRevision = sessionRevision;
    }

    [DataField(typeof(SessionFields), nameof(SessionFields.PublicId))]
    public Guid SessionPublicId { get; }

    [DataField(typeof(SessionFields), nameof(SessionFields.RotationRevision))]
    public long SessionRevision { get; }
}
