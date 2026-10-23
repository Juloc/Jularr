using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Authentication;

namespace Jularr.Backend.Data.User.Account.V1
{
    public sealed class ChangeAccountPasswordParametersV1 : BaseParameters
    {
    }

    // A signed-in person changes the password of their own account: the current one proves who asks.
    public sealed class ChangeAccountPasswordDataV1 : BaseData
    {
        public ChangeAccountPasswordDataV1(string currentPassword, string newPassword, string newPasswordConfirmation)
        {
            CurrentPassword = currentPassword;
            NewPassword = newPassword;
            NewPasswordConfirmation = newPasswordConfirmation;
        }

        [DataField(typeof(AuthenticationFields), nameof(AuthenticationFields.Password))]
        public string CurrentPassword { get; }

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
}
