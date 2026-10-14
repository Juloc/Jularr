using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Account;

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
        public SessionDetailsV1(Guid id, DateTime createdAt, DateTime expiresAt, bool isCurrent, string? deviceName, string? devicePlatform)
        {
            Id = id;
            CreatedAt = createdAt;
            ExpiresAt = expiresAt;
            IsCurrent = isCurrent;
            DeviceName = deviceName;
            DevicePlatform = devicePlatform;
        }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.PublicId))]
        public Guid Id { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.CreatedAt))]
        public DateTime CreatedAt { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.ExpiresAt))]
        public DateTime ExpiresAt { get; }

        public bool IsCurrent { get; }

        // A paired device; browser sign-ins have none.
        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.DeviceName))]
        public string? DeviceName { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.DevicePlatform))]
        public string? DevicePlatform { get; }
    }

    public sealed class RevokeSessionParametersV1 : BaseParameters
    {
        public RevokeSessionParametersV1(Guid sessionPublicId)
        {
            SessionPublicId = sessionPublicId;
        }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.PublicId))]
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
