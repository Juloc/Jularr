using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Account;

namespace Jularr.Backend.Data.Admin.Account.V1
{
    public sealed class ReadAccountSessionsParametersV1 : BaseParameters
    {
        public ReadAccountSessionsParametersV1(long accountId, int page = 1, int pageSize = 25)
        {
            AccountId = accountId;
            Page = page;
            PageSize = pageSize;
        }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.AccountId))]
        public long AccountId { get; }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class AccountSessionDetailsV1 : BaseResponse
    {
        public AccountSessionDetailsV1(Guid id, DateTime createdAt, DateTime expiresAt, bool isCurrent, string? deviceName, string? devicePlatform)
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

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.DeviceName))]
        public string? DeviceName { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.DevicePlatform))]
        public string? DevicePlatform { get; }
    }

    public sealed class RevokeAccountSessionParametersV1 : BaseParameters
    {
        public RevokeAccountSessionParametersV1(long accountId, Guid sessionPublicId)
        {
            AccountId = accountId;
            SessionPublicId = sessionPublicId;
        }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.AccountId))]
        public long AccountId { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.PublicId))]
        public Guid SessionPublicId { get; }
    }

    public sealed class RevokeAccountSessionResultV1 : BaseResponse, ISessionEndResult
    {
        public RevokeAccountSessionResultV1(bool endsCurrentSession)
        {
            EndsCurrentSession = endsCurrentSession;
        }

        public bool EndsCurrentSession { get; }
    }
}
