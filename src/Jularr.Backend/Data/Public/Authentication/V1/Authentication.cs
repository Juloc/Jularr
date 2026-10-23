using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Account;
using Jularr.Backend.Data.Fields.Authentication;
using Jularr.Backend.Data.Fields.Profile;
using Jularr.Backend.Data.Logic.Session;

namespace Jularr.Backend.Data.Public.Authentication.V1
{
    public sealed class AuthenticationParametersV1 : BaseParameters
    {
    }

    public sealed class LoginDataV1 : BaseData
    {
        public LoginDataV1(string username, string password)
        {
            Username = username;
            Password = password;
        }

        // The username or the email address of the account.
        [DataField(typeof(AuthenticationFields), nameof(AuthenticationFields.Login))]
        public string Username { get; }

        [DataField(typeof(AuthenticationFields), nameof(AuthenticationFields.Password))]
        public string Password { get; }
    }

    public sealed class RegisterDataV1 : BaseData
    {
        public RegisterDataV1(string username, string password, string passwordConfirmation, long uiLocaleId)
        {
            Username = username;
            Password = password;
            PasswordConfirmation = passwordConfirmation;
            UiLocaleId = uiLocaleId;
        }

        [DataField(typeof(AccountFields), nameof(AccountFields.Username))]
        public string Username { get; }

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

    public sealed class LoginResultV1 : BaseResponse, ISessionResult
    {
        internal LoginResultV1(SessionCredentialsData? session, bool requiresApproval = false)
            : this(session?.AccountId, session?.ProfileId, session?.ExpiresAt, requiresApproval)
        {
            Session = session;
        }

        [JsonConstructor]
        public LoginResultV1(long? accountId, long? profileId, DateTime? expiresAt, bool requiresApproval)
        {
            AccountId = accountId;
            ProfileId = profileId;
            ExpiresAt = expiresAt;
            RequiresApproval = requiresApproval;
        }

        public bool IsAuthenticated => AccountId.HasValue;
        public bool RequiresApproval { get; }
        public long? AccountId { get; }
        public long? ProfileId { get; }
        public DateTime? ExpiresAt { get; }

        // The shared HTTP adapter consumes committed credentials; JSON contains no secrets.
        [JsonIgnore]
        public SessionCredentialsData? Session { get; }
    }

    public sealed class RegisterResultV1 : BaseResponse
    {
        public bool RequiresApproval => true;
    }

    public sealed class AuthenticationResultV1 : BaseResponse
    {
        public AuthenticationResultV1(bool plexLoginEnabled, bool plexRegistrationEnabled, long? accountId, long? profileId, long? uiLocaleId = null, IReadOnlyList<string>? permissions = null)
        {
            PlexLoginEnabled = plexLoginEnabled;
            PlexRegistrationEnabled = plexRegistrationEnabled;
            AccountId = accountId;
            ProfileId = profileId;
            UiLocaleId = uiLocaleId;
            Permissions = permissions ?? [];
        }

        public bool PlexLoginEnabled { get; }
        public bool PlexRegistrationEnabled { get; }
        public bool IsAuthenticated => AccountId.HasValue;
        public long? AccountId { get; }
        public long? ProfileId { get; }
        public long? UiLocaleId { get; }
        public IReadOnlyList<string> Permissions { get; }
    }
}
