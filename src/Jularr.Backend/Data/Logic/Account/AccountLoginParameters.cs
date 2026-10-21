using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Account;
using Jularr.Backend.Data.Fields.Authentication;

namespace Jularr.Backend.Data.Logic.Account
{
    // The username or the email address of an account.
    public sealed class AccountLoginParameters : BaseData
    {
        public AccountLoginParameters(string login)
        {
            Login = login;
        }

        [DataField(typeof(AuthenticationFields), nameof(AuthenticationFields.Login))]
        public string Login { get; }
    }

    public sealed class AccountUsernameParameters : BaseData
    {
        public AccountUsernameParameters(string username)
        {
            Username = username;
        }

        [DataField(typeof(AccountFields), nameof(AccountFields.Username))]
        public string Username { get; }
    }
}
