using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Account;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Profile;

namespace Jularr.Backend.Data.Admin.Account.V1
{
    public sealed class AccountProfileMediaParametersV1 : BaseParameters
    {
        public AccountProfileMediaParametersV1(long accountId)
        {
            AccountId = accountId;
        }

        [DataField(typeof(AccountFields), nameof(AccountFields.Id))]
        public long AccountId { get; }
    }

    public sealed class ProfileMediaAccessParametersV1 : BaseParameters
    {
        public ProfileMediaAccessParametersV1(long profileId)
        {
            ProfileId = profileId;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }
    }

    // A blocked type or subtype is unavailable to the profile everywhere.
    public sealed class ProfileMediaAccessV1 : BaseResponse
    {
        public ProfileMediaAccessV1(long profileId, string key, bool isSubtype, bool isBlocked)
        {
            ProfileId = profileId;
            Key = key;
            IsSubtype = isSubtype;
            IsBlocked = isBlocked;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }

        [DataField(typeof(MediaCategoryFields), nameof(MediaCategoryFields.Key))]
        public string Key { get; }

        [DataField(typeof(MediaCategoryFields), nameof(MediaCategoryFields.IsSubtype))]
        public bool IsSubtype { get; }

        [DataField(typeof(MediaCategoryFields), nameof(MediaCategoryFields.IsBlocked))]
        public bool IsBlocked { get; }
    }

    public sealed class UpdateProfileMediaAccessDataV1 : BaseData
    {
        public UpdateProfileMediaAccessDataV1(string[] blockedKeys)
        {
            BlockedKeys = blockedKeys;
        }

        [DataField(typeof(MediaCategoryFields), nameof(MediaCategoryFields.Keys))]
        public string[] BlockedKeys { get; }

        public override void Validate()
        {
            base.Validate();
            if (!BlockedKeys.All(MediaCategories.IsKey))
            {
                throw new DataValidationException(nameof(BlockedKeys), "unknown_catalog_value");
            }
        }
    }
}
