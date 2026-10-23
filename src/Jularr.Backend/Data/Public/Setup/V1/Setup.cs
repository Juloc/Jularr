using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Account;
using Jularr.Backend.Data.Fields.Authentication;
using Jularr.Backend.Data.Fields.Profile;

namespace Jularr.Backend.Data.Public.Setup.V1
{
    public sealed class SetupParametersV1 : BaseParameters
    {
    }

    public sealed class ReadSetupResultV1 : BaseResponse
    {
        public ReadSetupResultV1(bool requiresOwner, bool isCompleted)
        {
            RequiresOwner = requiresOwner;
            IsCompleted = isCompleted;
        }

        public bool RequiresOwner { get; }

        // Setup is finished and the instance is in use.
        public bool IsCompleted { get; }
    }

    public sealed class CreateOwnerDataV1 : BaseData
    {
        public CreateOwnerDataV1(string username, string profileName, string password, string passwordConfirmation, long uiLocaleId)
        {
            Username = username;
            ProfileName = profileName;
            Password = password;
            PasswordConfirmation = passwordConfirmation;
            UiLocaleId = uiLocaleId;
        }

        [DataField(typeof(AccountFields), nameof(AccountFields.Username))]
        public string Username { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.DisplayName), true, 100)]
        public string ProfileName { get; }

        [DataField(typeof(AuthenticationFields), nameof(AuthenticationFields.NewPassword))]
        public string Password { get; }

        [DataField(typeof(AuthenticationFields), nameof(AuthenticationFields.NewPassword))]
        public string PasswordConfirmation { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.UiLocaleId))]
        public long UiLocaleId { get; }

        public override void Validate()
        {
            base.Validate();

            // Confirm before any mutation.
            if (!string.Equals(Password, PasswordConfirmation, StringComparison.Ordinal))
            {
                throw new DataValidationException(nameof(PasswordConfirmation), "password_mismatch");
            }
        }
    }
}
