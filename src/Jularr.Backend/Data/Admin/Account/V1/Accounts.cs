using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Data.Fields.Account;

namespace Jularr.Backend.Data.Admin.Account.V1
{
    public sealed class ReadAccountsParametersV1 : BaseParameters
    {
        public ReadAccountsParametersV1(int page = 1, int pageSize = 25)
        {
            Page = page;
            PageSize = pageSize;
        }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class AccountParametersV1 : BaseParameters
    {
        public AccountParametersV1(long accountId)
        {
            AccountId = accountId;
        }

        [DataField(typeof(AccountFields), nameof(AccountFields.Id))]
        public long AccountId { get; }
    }

    public sealed class AccountDetailsV1 : BaseResponse
    {
        public AccountDetailsV1(long id, string email, string displayName, AccountRoleType accountRoleTypeId, bool isEnabled, DateTime? emailVerifiedAt, DateTime updatedAt)
        {
            Id = id;
            Email = email;
            DisplayName = displayName;
            AccountRoleTypeId = accountRoleTypeId;
            IsEnabled = isEnabled;
            EmailVerifiedAt = emailVerifiedAt;
            UpdatedAt = updatedAt;
        }

        [DataField(typeof(AccountFields), nameof(AccountFields.Id))]
        public long Id { get; }

        [DataField(typeof(AccountFields), nameof(AccountFields.Email))]
        public string Email { get; }

        [DataField(typeof(AccountFields), nameof(AccountFields.DisplayName))]
        public string DisplayName { get; }

        [DataField(typeof(AccountFields), nameof(AccountFields.AccountRoleTypeId))]
        public AccountRoleType AccountRoleTypeId { get; }

        [DataField(typeof(AccountFields), nameof(AccountFields.IsEnabled))]
        public bool IsEnabled { get; }

        [DataField(typeof(AccountFields), nameof(AccountFields.EmailVerifiedAt))]
        public DateTime? EmailVerifiedAt { get; }

        [DataField(typeof(AccountFields), nameof(AccountFields.UpdatedAt))]
        public DateTime UpdatedAt { get; }
    }

    public sealed class UpdateAccountDataV1 : BaseData
    {
        public UpdateAccountDataV1(string displayName, AccountRoleType accountRoleTypeId, bool isEnabled)
        {
            DisplayName = displayName;
            AccountRoleTypeId = accountRoleTypeId;
            IsEnabled = isEnabled;
        }

        [DataField(typeof(AccountFields), nameof(AccountFields.DisplayName))]
        public string DisplayName { get; }

        [DataField(typeof(AccountFields), nameof(AccountFields.AccountRoleTypeId))]
        public AccountRoleType AccountRoleTypeId { get; }

        [DataField(typeof(AccountFields), nameof(AccountFields.IsEnabled))]
        public bool IsEnabled { get; }
    }
}
