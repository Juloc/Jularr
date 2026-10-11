using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Account;

namespace Jularr.Backend.Data.Logic.Account
{
    public sealed class AccountEmailParameters : BaseData
    {
        public AccountEmailParameters(string email)
        {
            Email = email;
        }

        [DataField(typeof(AccountFields), nameof(AccountFields.Email))]
        public string Email { get; }
    }
}
