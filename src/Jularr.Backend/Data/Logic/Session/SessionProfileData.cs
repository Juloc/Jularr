using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Profile;
using Jularr.Backend.Data.Fields.Account;

namespace Jularr.Backend.Data.Logic.Session
{
    public sealed class SessionProfileData : BaseData
    {
        public SessionProfileData(long profileId, string? pinHash, DateTime expiresAt, long credentialRevision)
        {
            ProfileId = profileId;
            PinHash = pinHash;
            ExpiresAt = expiresAt;
            CredentialRevision = credentialRevision;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [JsonIgnore]
        [DataField(typeof(ProfileFields), nameof(ProfileFields.PinHash))]
        public string? PinHash { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.ExpiresAt))]
        public DateTime ExpiresAt { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.CredentialRevision))]
        public long CredentialRevision { get; }
    }
}
