using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Session;

namespace Jularr.Backend.Data.User.Session.V1
{
    public sealed class SessionParametersV1 : BaseParameters
    {
    }

    public sealed class ReadSessionsParametersV1 : BaseParameters
    {
        public ReadSessionsParametersV1(int page = 1, int pageSize = 25)
        {
            Page = page;
            PageSize = pageSize;
        }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class SessionDetailsV1 : BaseResponse
    {
        public SessionDetailsV1(Guid id, DateTime createdAt, DateTime expiresAt, bool isCurrent)
        {
            Id = id;
            CreatedAt = createdAt;
            ExpiresAt = expiresAt;
            IsCurrent = isCurrent;
        }

        [DataField(typeof(SessionFields), nameof(SessionFields.PublicId))]
        public Guid Id { get; }

        [DataField(typeof(SessionFields), nameof(SessionFields.CreatedAt))]
        public DateTime CreatedAt { get; }

        [DataField(typeof(SessionFields), nameof(SessionFields.ExpiresAt))]
        public DateTime ExpiresAt { get; }

        public bool IsCurrent { get; }
    }

    public sealed class RevokeSessionParametersV1 : BaseParameters
    {
        public RevokeSessionParametersV1(Guid sessionPublicId)
        {
            SessionPublicId = sessionPublicId;
        }

        [DataField(typeof(SessionFields), nameof(SessionFields.PublicId))]
        public Guid SessionPublicId { get; }
    }

    public sealed class RevokeSessionResultV1 : BaseResponse, ISessionEndResult
    {
        public RevokeSessionResultV1(bool endsCurrentSession)
        {
            EndsCurrentSession = endsCurrentSession;
        }

        public bool EndsCurrentSession { get; }
    }

    public sealed class LogoutResultV1 : BaseResponse, ISessionEndResult
    {
        public bool IsAuthenticated => false;
        public bool EndsCurrentSession => true;
    }
}
