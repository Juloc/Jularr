using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Profile;

namespace Jularr.Backend.Data.User.Profile.V1
{
    public sealed class PwaAppearanceParametersV1 : BaseParameters
    {
    }

    // Only the signed-in account's main profile is included; no profile IDs are ever sent in icon URLs.
    public sealed class PwaAppearanceV1 : BaseResponse
    {
        public PwaAppearanceV1(long? mainProfileId, string accentHex)
        {
            MainProfileId = mainProfileId;
            AccentHex = accentHex;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id), false)]
        public long? MainProfileId { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.AccentHex))]
        public string AccentHex { get; }
    }

    public sealed class SavePwaAccentDataV1 : BaseData
    {
        public SavePwaAccentDataV1(string accentHex) => AccentHex = accentHex;

        [DataField(typeof(ProfileFields), nameof(ProfileFields.AccentHex))]
        public string AccentHex { get; }
    }
}
