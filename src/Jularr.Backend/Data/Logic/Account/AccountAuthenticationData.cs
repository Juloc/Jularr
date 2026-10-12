using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Data.Fields.Account;

namespace Jularr.Backend.Data.Logic.Account
{
    public sealed class AccountAuthenticationData : BaseData
    {
        public AccountAuthenticationData(long id, long credentialRevision, string? passwordHash, bool isEnabled, bool requiresSecondFactor)
        {
            Id = id;
            CredentialRevision = credentialRevision;
            PasswordHash = passwordHash;
            IsEnabled = isEnabled;
            RequiresSecondFactor = requiresSecondFactor;
        }

        [DataField(typeof(AccountFields), nameof(AccountFields.Id))]
        public long Id { get; }

        [DataField(typeof(AccountFields), nameof(AccountFields.CredentialRevision))]
        public long CredentialRevision { get; }

        [JsonIgnore]
        [DataField(typeof(AccountPasswordFields), nameof(AccountPasswordFields.PasswordHash), false)]
        public string? PasswordHash { get; }

        [DataField(typeof(AccountFields), nameof(AccountFields.IsEnabled))]
        public bool IsEnabled { get; }
        public bool RequiresSecondFactor { get; }
    }

    public sealed class AccountCreationData : BaseData
    {
        public AccountCreationData(string email, string displayName, bool isEnabled, DateTime? emailVerifiedAt, AccountRoleType accountRoleTypeId = AccountRoleType.User)
        {
            Email = email;
            DisplayName = displayName;
            IsEnabled = isEnabled;
            EmailVerifiedAt = emailVerifiedAt;
            AccountRoleTypeId = accountRoleTypeId;
        }

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
    }

    public sealed class AccountIdData : BaseData
    {
        public AccountIdData(long id)
        {
            Id = id;
        }

        [DataField(typeof(AccountFields), nameof(AccountFields.Id))]
        public long Id { get; }
    }

    public sealed class AccountPasswordData : BaseData
    {
        public AccountPasswordData(long accountId, string passwordHash)
        {
            AccountId = accountId;
            PasswordHash = passwordHash;
        }

        [DataField(typeof(AccountPasswordFields), nameof(AccountPasswordFields.AccountId))]
        public long AccountId { get; }

        [JsonIgnore]
        [DataField(typeof(AccountPasswordFields), nameof(AccountPasswordFields.PasswordHash))]
        public string PasswordHash { get; }
    }
}
