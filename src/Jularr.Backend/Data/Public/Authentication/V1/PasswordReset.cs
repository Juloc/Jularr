using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Authentication;

namespace Jularr.Backend.Data.Public.Authentication.V1
{
    public sealed class ResetPasswordDataV1 : BaseData
    {
        public ResetPasswordDataV1(string token, string newPassword, string newPasswordConfirmation)
        {
            Token = token;
            NewPassword = newPassword;
            NewPasswordConfirmation = newPasswordConfirmation;
        }

        // 32 random bytes, base64url without padding: 43 characters.
        [DataField(typeof(AuthenticationFields), nameof(AuthenticationFields.BrowserNonce))]
        public string Token { get; }

        [DataField(typeof(AuthenticationFields), nameof(AuthenticationFields.NewPassword))]
        public string NewPassword { get; }

        [DataField(typeof(AuthenticationFields), nameof(AuthenticationFields.NewPassword))]
        public string NewPasswordConfirmation { get; }

        public override void Validate()
        {
            base.Validate();

            // Confirm before any mutation.
            if (!string.Equals(NewPassword, NewPasswordConfirmation, StringComparison.Ordinal))
            {
                throw new DataValidationException(nameof(NewPasswordConfirmation), "password_mismatch");
            }
        }
    }

    public sealed class ResetPasswordResultV1 : BaseResponse
    {
    }
}
