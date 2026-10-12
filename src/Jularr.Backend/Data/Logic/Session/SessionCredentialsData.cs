using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Account;

namespace Jularr.Backend.Data.Logic.Session
{
    public sealed class SessionCredentialsData : BaseData
    {
        public SessionCredentialsData(Guid publicId, long accountId, long? profileId, byte[] tokenHash,
            string token, long credentialRevision, DateTime expiresAt, long rotationRevision = 1)
        {
            PublicId = publicId;
            AccountId = accountId;
            ProfileId = profileId;
            TokenHash = tokenHash;
            Token = token;
            CredentialRevision = credentialRevision;
            ExpiresAt = expiresAt;
            RotationRevision = rotationRevision;
        }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.PublicId))]
        public Guid PublicId { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.AccountId))]
        public long AccountId { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.ActiveProfileId))]
        public long? ProfileId { get; }

        [JsonIgnore]
        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.TokenHash))]
        public byte[] TokenHash { get; }

        [JsonIgnore]
        public string Token { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.CredentialRevision))]
        public long CredentialRevision { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.ExpiresAt))]
        public DateTime ExpiresAt { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.RotationRevision))]
        public long RotationRevision { get; }
    }
}
