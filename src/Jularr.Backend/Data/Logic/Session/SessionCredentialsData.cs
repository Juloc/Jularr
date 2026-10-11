using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Session;

namespace Jularr.Backend.Data.Logic.Session
{
    public sealed class SessionCredentialsData : BaseData
    {
        public SessionCredentialsData(Guid publicId, long accountId, long? profileId, byte[] tokenHash,
            string token, long credentialRevision, DateTime expiresAt)
        {
            PublicId = publicId;
            AccountId = accountId;
            ProfileId = profileId;
            TokenHash = tokenHash;
            Token = token;
            CredentialRevision = credentialRevision;
            ExpiresAt = expiresAt;
        }

        [DataField(typeof(SessionFields), nameof(SessionFields.PublicId))]
        public Guid PublicId { get; }

        [DataField(typeof(SessionFields), nameof(SessionFields.AccountId))]
        public long AccountId { get; }

        [DataField(typeof(SessionFields), nameof(SessionFields.ActiveProfileId))]
        public long? ProfileId { get; }

        [JsonIgnore]
        [DataField(typeof(SessionFields), nameof(SessionFields.TokenHash))]
        public byte[] TokenHash { get; }

        [JsonIgnore]
        public string Token { get; }

        [DataField(typeof(SessionFields), nameof(SessionFields.CredentialRevision))]
        public long CredentialRevision { get; }

        [DataField(typeof(SessionFields), nameof(SessionFields.ExpiresAt))]
        public DateTime ExpiresAt { get; }

        public long RotationRevision => 1;
    }
}
