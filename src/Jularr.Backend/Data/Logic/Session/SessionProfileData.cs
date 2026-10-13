using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Profile;
using Jularr.Backend.Data.Fields.Account;

namespace Jularr.Backend.Data.Logic.Session
{
    public sealed class SessionProfileData : BaseData
    {
        public SessionProfileData(long profileId, string? pinHash, bool isPinLocked, DateTime expiresAt, long credentialRevision)
        {
            ProfileId = profileId;
            PinHash = pinHash;
            IsPinLocked = isPinLocked;
            ExpiresAt = expiresAt;
            CredentialRevision = credentialRevision;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [JsonIgnore]
        [DataField(typeof(ProfileFields), nameof(ProfileFields.PinHash))]
        public string? PinHash { get; }

        // Five failed PINs lock the profile for a minute.
        public bool IsPinLocked { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.ExpiresAt))]
        public DateTime ExpiresAt { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.CredentialRevision))]
        public long CredentialRevision { get; }
    }
}
