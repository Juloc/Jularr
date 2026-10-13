using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Profile;

namespace Jularr.Backend.Data.Logic.Profile
{
    public sealed class ProfilePinData : BaseData
    {
        public ProfilePinData(long profileId, string? pinHash, int? pinLength)
        {
            ProfileId = profileId;
            PinHash = pinHash;
            PinLength = pinLength;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.PinHash))]
        public string? PinHash { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.PinLength))]
        public int? PinLength { get; }
    }
}
