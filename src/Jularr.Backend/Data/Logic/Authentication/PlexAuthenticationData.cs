using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Data.Fields.Account;
using Jularr.Backend.Data.Fields.Authentication;

namespace Jularr.Backend.Data.Logic.Authentication
{
    public sealed class PlexChallengeParameters : BaseData
    {
        public PlexChallengeParameters(Guid authChallengePublicId, string browserNonce)
        {
            AuthChallengePublicId = authChallengePublicId;
            BrowserNonce = browserNonce;
        }

        [DataField(typeof(AccountAuthChallengeFields), nameof(AccountAuthChallengeFields.PublicId))]
        public Guid AuthChallengePublicId { get; }

        [JsonIgnore]
        [DataField(typeof(AuthenticationFields), nameof(AuthenticationFields.BrowserNonce))]
        public string BrowserNonce { get; }
    }

    public sealed class PlexFlowData : BaseData
    {
        public PlexFlowData(long id, long providerId, long externalPinId, string clientIdentifier, byte[] tokenHash, byte[] protectedState)
        {
            Id = id;
            ProviderId = providerId;
            ExternalPinId = externalPinId;
            ClientIdentifier = clientIdentifier;
            TokenHash = tokenHash;
            ProtectedState = protectedState;
        }

        [DataField(typeof(AccountAuthChallengeFields), nameof(AccountAuthChallengeFields.Id))]
        public long Id { get; }

        [DataField(typeof(AccountExternalAuthFlowFields), nameof(AccountExternalAuthFlowFields.ProviderId))]
        public long ProviderId { get; }

        [DataField(typeof(AccountExternalAuthFlowFields), nameof(AccountExternalAuthFlowFields.ExternalPinId))]
        public long ExternalPinId { get; }

        [DataField(typeof(AccountExternalAuthFlowFields), nameof(AccountExternalAuthFlowFields.ClientIdentifier))]
        public string ClientIdentifier { get; }

        [JsonIgnore]
        [DataField(typeof(AccountAuthChallengeFields), nameof(AccountAuthChallengeFields.TokenHash))]
        public byte[] TokenHash { get; }

        [JsonIgnore]
        [DataField(typeof(AccountAuthChallengeFields), nameof(AccountAuthChallengeFields.ProtectedState), true)]
        public byte[] ProtectedState { get; }
    }

    public sealed class PlexChallengeCreationData : BaseData
    {
        public PlexChallengeCreationData(Guid publicId, byte[] tokenHash, byte[] protectedState, DateTime expiresAt)
        {
            PublicId = publicId;
            TokenHash = tokenHash;
            ProtectedState = protectedState;
            ExpiresAt = expiresAt;
        }

        [DataField(typeof(AccountAuthChallengeFields), nameof(AccountAuthChallengeFields.PublicId))]
        public Guid PublicId { get; }

        [DataField(typeof(AccountAuthChallengeFields), nameof(AccountAuthChallengeFields.AuthChallengePurposeTypeId))]
        public AuthChallengePurposeType AuthChallengePurposeTypeId => AuthChallengePurposeType.ExternalLogin;

        [JsonIgnore]
        [DataField(typeof(AccountAuthChallengeFields), nameof(AccountAuthChallengeFields.TokenHash))]
        public byte[] TokenHash { get; }

        [JsonIgnore]
        [DataField(typeof(AccountAuthChallengeFields), nameof(AccountAuthChallengeFields.ProtectedState), true)]
        public byte[] ProtectedState { get; }

        [DataField(typeof(AccountAuthChallengeFields), nameof(AccountAuthChallengeFields.ExpiresAt))]
        public DateTime ExpiresAt { get; }
    }

    public sealed class PlexFlowCreationData : BaseData
    {
        public PlexFlowCreationData(long accountAuthChallengeId, long providerId, long externalPinId, string clientIdentifier)
        {
            AccountAuthChallengeId = accountAuthChallengeId;
            ProviderId = providerId;
            ExternalPinId = externalPinId;
            ClientIdentifier = clientIdentifier;
        }

        [DataField(typeof(AccountExternalAuthFlowFields), nameof(AccountExternalAuthFlowFields.AccountAuthChallengeId))]
        public long AccountAuthChallengeId { get; }

        [DataField(typeof(AccountExternalAuthFlowFields), nameof(AccountExternalAuthFlowFields.AuthChallengePurposeTypeId))]
        public AuthChallengePurposeType AuthChallengePurposeTypeId => AuthChallengePurposeType.ExternalLogin;

        [DataField(typeof(AccountExternalAuthFlowFields), nameof(AccountExternalAuthFlowFields.ProviderId))]
        public long ProviderId { get; }

        [DataField(typeof(AccountExternalAuthFlowFields), nameof(AccountExternalAuthFlowFields.ExternalPinId))]
        public long ExternalPinId { get; }

        [DataField(typeof(AccountExternalAuthFlowFields), nameof(AccountExternalAuthFlowFields.ClientIdentifier))]
        public string ClientIdentifier { get; }
    }

    public sealed class PlexIdentityData : BaseData
    {
        public PlexIdentityData(string externalAccountId, string email, string displayName, bool emailVerified, string accessToken)
        {
            ExternalAccountId = externalAccountId;
            Email = email;
            DisplayName = displayName;
            EmailVerified = emailVerified;
            AccessToken = accessToken;
        }

        [DataField(typeof(AccountExternalLoginFields), nameof(AccountExternalLoginFields.ExternalAccountId))]
        public string ExternalAccountId { get; }

        public string Email { get; }
        public string DisplayName { get; }
        public bool EmailVerified { get; }
        [JsonIgnore]
        public string AccessToken { get; }
    }

    public sealed class ExternalLoginData : BaseData
    {
        public ExternalLoginData(long providerId, string externalAccountId, long accountId)
        {
            ProviderId = providerId;
            ExternalAccountId = externalAccountId;
            AccountId = accountId;
        }

        [DataField(typeof(AccountExternalLoginFields), nameof(AccountExternalLoginFields.ProviderId))]
        public long ProviderId { get; }

        [DataField(typeof(AccountExternalLoginFields), nameof(AccountExternalLoginFields.ExternalAccountId))]
        public string ExternalAccountId { get; }

        [DataField(typeof(AccountExternalLoginFields), nameof(AccountExternalLoginFields.AccountId))]
        public long AccountId { get; }
    }
}
