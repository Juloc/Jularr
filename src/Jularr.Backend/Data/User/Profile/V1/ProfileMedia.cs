using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.User.Profile.V1
{
    // A media type or subtype the profile may use; hidden ones leave its lists.
    public sealed class ProfileMediaV1 : BaseResponse
    {
        public ProfileMediaV1(string key, bool isSubtype, bool isHidden)
        {
            Key = key;
            IsSubtype = isSubtype;
            IsHidden = isHidden;
        }

        [DataField(typeof(MediaCategoryFields), nameof(MediaCategoryFields.Key))]
        public string Key { get; }

        [DataField(typeof(MediaCategoryFields), nameof(MediaCategoryFields.IsSubtype))]
        public bool IsSubtype { get; }

        [DataField(typeof(MediaCategoryFields), nameof(MediaCategoryFields.IsHidden))]
        public bool IsHidden { get; }
    }

    public sealed class UpdateProfileMediaDataV1 : BaseData
    {
        public UpdateProfileMediaDataV1(string[] hiddenKeys)
        {
            HiddenKeys = hiddenKeys;
        }

        [DataField(typeof(MediaCategoryFields), nameof(MediaCategoryFields.Keys))]
        public string[] HiddenKeys { get; }

        public override void Validate()
        {
            base.Validate();
            if (!HiddenKeys.All(MediaCategories.IsKey))
            {
                throw new DataValidationException(nameof(HiddenKeys), "unknown_catalog_value");
            }
        }
    }
}
