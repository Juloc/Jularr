using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Account;
using Jularr.Backend.Data.Fields.Account;

namespace Jularr.Backend.Data.Logic.Authentication
{
    // A one-time link with which a person sets a new password: only the hash of its token is stored.
    public sealed class PasswordResetCreationData : BaseData
    {
        public PasswordResetCreationData(long accountId, byte[] tokenHash, DateTime expiresAt)
        {
            AccountId = accountId;
            TokenHash = tokenHash;
            ExpiresAt = expiresAt;
        }

        [DataField(typeof(AccountAuthChallengeFields), nameof(AccountAuthChallengeFields.AccountId))]
        public long? AccountId { get; }

        [DataField(typeof(AccountAuthChallengeFields), nameof(AccountAuthChallengeFields.AuthChallengePurposeTypeId))]
        public AuthChallengePurposeType AuthChallengePurposeTypeId => AuthChallengePurposeType.PasswordReset;

        [JsonIgnore]
        [DataField(typeof(AccountAuthChallengeFields), nameof(AccountAuthChallengeFields.TokenHash))]
        public byte[] TokenHash { get; }

        [DataField(typeof(AccountAuthChallengeFields), nameof(AccountAuthChallengeFields.ExpiresAt))]
        public DateTime ExpiresAt { get; }
    }

    public sealed class PasswordResetTokenData : BaseData
    {
        public PasswordResetTokenData(byte[] tokenHash)
        {
            TokenHash = tokenHash;
        }

        [JsonIgnore]
        [DataField(typeof(AccountAuthChallengeFields), nameof(AccountAuthChallengeFields.TokenHash))]
        public byte[] TokenHash { get; }
    }

    // The open reset a token belongs to.
    public sealed class PasswordResetClaimData : BaseData
    {
        public PasswordResetClaimData(long id, long accountId)
        {
            Id = id;
            AccountId = accountId;
        }

        [DataField(typeof(AccountAuthChallengeFields), nameof(AccountAuthChallengeFields.Id))]
        public long Id { get; }

        [DataField(typeof(AccountPasswordFields), nameof(AccountPasswordFields.AccountId))]
        public long AccountId { get; }
    }
}
