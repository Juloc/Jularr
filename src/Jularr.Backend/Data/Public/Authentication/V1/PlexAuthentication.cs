using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Account;

namespace Jularr.Backend.Data.Public.Authentication.V1
{
    public sealed class CompletePlexParametersV1 : BaseParameters
    {
        public CompletePlexParametersV1(Guid authChallengePublicId)
        {
            AuthChallengePublicId = authChallengePublicId;
        }

        [DataField(typeof(AccountAuthChallengeFields), nameof(AccountAuthChallengeFields.PublicId))]
        public Guid AuthChallengePublicId { get; }
    }

    public sealed class StartPlexResultV1 : BaseResponse, IAuthenticationChallengeResult
    {
        internal StartPlexResultV1(Guid authChallengePublicId, string authorizationUrl, string browserNonce, DateTime expiresAt)
            : this(authChallengePublicId, authorizationUrl, expiresAt)
        {
            BrowserNonce = browserNonce;
        }

        [JsonConstructor]
        public StartPlexResultV1(Guid authChallengePublicId, string authorizationUrl, DateTime expiresAt)
        {
            AuthChallengePublicId = authChallengePublicId;
            AuthorizationUrl = authorizationUrl;
            ExpiresAt = expiresAt;
        }

        [DataField(typeof(AccountAuthChallengeFields), nameof(AccountAuthChallengeFields.PublicId))]
        public Guid AuthChallengePublicId { get; }

        public string AuthorizationUrl { get; }
        public DateTime ExpiresAt { get; }

        [JsonIgnore]
        public string BrowserNonce { get; } = "";
    }
}
