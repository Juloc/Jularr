using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Data.Fields.Account;

namespace Jularr.Backend.Data.Logic.Account
{
    internal sealed class AccountUpdateData : BaseData
    {
        public AccountUpdateData(long id, string displayName, AccountRoleType accountRoleTypeId, bool isEnabled)
        {
            Id = id;
            DisplayName = displayName;
            AccountRoleTypeId = accountRoleTypeId;
            IsEnabled = isEnabled;
        }

        [DataField(typeof(AccountFields), nameof(AccountFields.Id))]
        public long Id { get; set; }

        [DataField(typeof(AccountFields), nameof(AccountFields.DisplayName))]
        public string DisplayName { get; set; }

        [DataField(typeof(AccountFields), nameof(AccountFields.AccountRoleTypeId))]
        public AccountRoleType AccountRoleTypeId { get; set; }

        [DataField(typeof(AccountFields), nameof(AccountFields.IsEnabled))]
        public bool IsEnabled { get; set; }
    }
}
