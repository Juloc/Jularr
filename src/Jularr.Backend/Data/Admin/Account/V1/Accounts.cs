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
        public AccountDetailsV1(long id, string username, string? email, string displayName, AccountRoleType accountRoleTypeId, bool isEnabled, DateTime? emailVerifiedAt, DateTime updatedAt,
            bool mayConfirmSources = false, bool transcodingPriority = false)
        {
            MayConfirmSources = mayConfirmSources;
            TranscodingPriority = transcodingPriority;
            Id = id;
            Username = username;
            Email = email;
            DisplayName = displayName;
            AccountRoleTypeId = accountRoleTypeId;
            IsEnabled = isEnabled;
            EmailVerifiedAt = emailVerifiedAt;
            UpdatedAt = updatedAt;
        }

        [DataField(typeof(AccountFields), nameof(AccountFields.Id))]
        public long Id { get; }

        [DataField(typeof(AccountFields), nameof(AccountFields.Username))]
        public string Username { get; }

        [DataField(typeof(AccountFields), nameof(AccountFields.Email), false)]
        public string? Email { get; }

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

        // May confirm source checks for own requests when the instance allows it.
        [DataField(typeof(AccountFields), nameof(AccountFields.MayConfirmSources))]
        public bool MayConfirmSources { get; }

        // A conversion of this account may stop the newest one of an account without priority when the server is full.
        [DataField(typeof(AccountFields), nameof(AccountFields.TranscodingPriority))]
        public bool TranscodingPriority { get; }
    }

    public sealed class UpdateAccountDataV1 : BaseData
    {
        public UpdateAccountDataV1(string displayName, AccountRoleType accountRoleTypeId, bool isEnabled, bool? mayConfirmSources = null, bool? transcodingPriority = null)
        {
            MayConfirmSources = mayConfirmSources;
            TranscodingPriority = transcodingPriority;
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

        // Empty keeps the current switch.
        [DataField(typeof(AccountFields), nameof(AccountFields.MayConfirmSources), false)]
        public bool? MayConfirmSources { get; }

        [DataField(typeof(AccountFields), nameof(AccountFields.TranscodingPriority), false)]
        public bool? TranscodingPriority { get; }
    }
}
