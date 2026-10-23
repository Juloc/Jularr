using Jularr.Backend.Data.Common;

namespace Jularr.Backend.Data.Admin.Account.V1
{
    // The token appears once, in the answer to the administrator who asked for it.
    public sealed class AccountPasswordLinkResultV1 : BaseResponse
    {
        public AccountPasswordLinkResultV1(string token, DateTime expiresAt)
        {
            Token = token;
            ExpiresAt = expiresAt;
        }

        public string Token { get; }

        public DateTime ExpiresAt { get; }
    }
}
